using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Reflection;
using System.Reflection.Emit;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: Deep.ReleaseCompositionVerifier <Deep.Client.Maui.dll>");
    return 2;
}

var assemblyPath = Path.GetFullPath(args[0]);
if (!File.Exists(assemblyPath))
{
    Console.Error.WriteLine($"Release assembly was not found: {assemblyPath}");
    return 2;
}

try
{
    using var stream = File.OpenRead(assemblyPath);
    using var peReader = new PEReader(stream);
    if (!peReader.HasMetadata)
    {
        throw new InvalidDataException("Release output has no managed metadata.");
    }

    var reader = peReader.GetMetadataReader();
    var programType = reader.TypeDefinitions
        .Select(reader.GetTypeDefinition)
        .Single(type =>
            reader.GetString(type.Namespace) == "Deep.Client.Maui" &&
            reader.GetString(type.Name) == "MauiProgram");
    var createMauiApp = programType.GetMethods()
        .Select(reader.GetMethodDefinition)
        .Single(method => reader.GetString(method.Name) == "CreateMauiApp");
    if (createMauiApp.RelativeVirtualAddress == 0)
    {
        throw new InvalidDataException("MauiProgram.CreateMauiApp has no executable method body.");
    }

    var body = peReader.GetMethodBody(createMauiApp.RelativeVirtualAddress);
    var opcodeMap = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static field => field.FieldType == typeof(OpCode))
        .Select(static field => (OpCode)field.GetValue(null)!)
        .ToDictionary(static opcode => unchecked((ushort)opcode.Value));
    var il = body.GetILBytes() ??
        throw new InvalidDataException("MauiProgram.CreateMauiApp has no IL stream.");
    var calls = ReadCalledMembers(il, reader, opcodeMap)
        .ToHashSet(StringComparer.Ordinal);
    var requiredCalls = new[]
    {
        "Deep.Client.Maui.Core.Services.RoutedProductionCompositionFactory::Create",
        "Deep.Client.Maui.Core.Services.RoutedProductionComposition::get_Router",
        "Deep.Client.Maui.Core.Services.RoutedProductionComposition::get_RouteProvider",
        "Deep.Client.Maui.Core.Services.RoutedProductionComposition::get_SessionMessageTransport"
    };
    var missing = requiredCalls.Where(required => !calls.Contains(required)).ToArray();
    if (missing.Length != 0)
    {
        throw new InvalidDataException(
            "Compiled MauiProgram release composition is missing required routed bindings: " +
            string.Join(", ", missing));
    }

    Console.WriteLine("Compiled MauiProgram release routed composition verified.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static IReadOnlyList<string> ReadCalledMembers(
    byte[] il,
    MetadataReader reader,
    IReadOnlyDictionary<ushort, OpCode> opcodeMap)
{
    var calls = new List<string>();
    var offset = 0;
    while (offset < il.Length)
    {
        var opcodeValue = ReadOpcode(il, ref offset);
        if (!opcodeMap.TryGetValue(opcodeValue, out var opcode))
        {
            throw new InvalidDataException($"Unknown IL opcode 0x{opcodeValue:x4}.");
        }

        if (opcode.OperandType == OperandType.InlineMethod)
        {
            EnsureAvailable(il, offset, sizeof(int));
            var token = BitConverter.ToInt32(il, offset);
            offset += sizeof(int);
            calls.Add(DescribeMethod(MetadataTokens.EntityHandle(token), reader));
            continue;
        }

        offset += GetOperandSize(opcode.OperandType, il, offset);
    }

    return calls;
}

static ushort ReadOpcode(byte[] il, ref int offset)
{
    EnsureAvailable(il, offset, 1);
    var first = il[offset++];
    if (first != 0xfe)
    {
        return first;
    }

    EnsureAvailable(il, offset, 1);
    return unchecked((ushort)(0xfe00 | il[offset++]));
}

static int GetOperandSize(OperandType operandType, byte[] il, int offset)
{
    var size = operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineI or OperandType.ShortInlineBrTarget or
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineString or
        OperandType.InlineType or OperandType.InlineField or OperandType.InlineSig or
        OperandType.InlineTok or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => SwitchSize(il, offset),
        _ => throw new InvalidDataException($"Unsupported IL operand type: {operandType}")
    };
    EnsureAvailable(il, offset, size);
    return size;
}

static int SwitchSize(byte[] il, int offset)
{
    EnsureAvailable(il, offset, sizeof(int));
    var count = BitConverter.ToInt32(il, offset);
    if (count < 0)
    {
        throw new InvalidDataException("Invalid switch operand.");
    }

    return checked(sizeof(int) + (count * sizeof(int)));
}

static string DescribeMethod(EntityHandle handle, MetadataReader reader)
{
    if (handle.Kind == HandleKind.MethodSpecification)
    {
        return DescribeMethod(reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method, reader);
    }

    if (handle.Kind == HandleKind.MemberReference)
    {
        var member = reader.GetMemberReference((MemberReferenceHandle)handle);
        return $"{DescribeType(member.Parent, reader)}::{reader.GetString(member.Name)}";
    }

    if (handle.Kind == HandleKind.MethodDefinition)
    {
        var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
        return $"{DescribeType(method.GetDeclaringType(), reader)}::{reader.GetString(method.Name)}";
    }

    return handle.Kind.ToString();
}

static string DescribeType(EntityHandle handle, MetadataReader reader)
{
    if (handle.Kind == HandleKind.TypeReference)
    {
        var type = reader.GetTypeReference((TypeReferenceHandle)handle);
        return JoinTypeName(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }

    if (handle.Kind == HandleKind.TypeDefinition)
    {
        var type = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
        return JoinTypeName(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }

    return handle.Kind.ToString();
}

static string JoinTypeName(string @namespace, string name) =>
    string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";

static void EnsureAvailable(byte[] il, int offset, int size)
{
    if (offset < 0 || size < 0 || offset > il.Length - size)
    {
        throw new InvalidDataException("Invalid or truncated IL stream.");
    }
}
