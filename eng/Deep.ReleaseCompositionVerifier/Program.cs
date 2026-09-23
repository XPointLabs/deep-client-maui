using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: Deep.ReleaseCompositionVerifier <compiled Deep.Client.Maui.dll>");
    return 1;
}

try
{
    AssertControlFlowFixtures();
    var assemblyPath = Path.GetFullPath(args[0]);
    var assemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
    AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        var dependency = Path.Combine(assemblyDirectory, name.Name + ".dll");
        return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
    };
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
    Require(assembly.GetName().Name == "Deep.Client.Maui", "The selected assembly is not Deep.Client.Maui.");
    var program = RequiredType(assembly, "Deep.Client.Maui.MauiProgram");
    var app = RequiredType(assembly, "Deep.Client.Maui.App");
    _ = RequiredType(assembly, "Deep.Client.Maui.AppShell");
    var entrypoint = program.GetMethod("CreateMauiApp", BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("The clean MAUI entrypoint is missing.");
    Require(entrypoint.GetParameters().Length == 0,
        "The clean MAUI entrypoint has unexpected parameters.");
    Require(program.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        .All(method => method.Name != "ConfigureApplicationServices"),
        "The retired Session-era composition entrypoint is still compiled.");

    var instructions = ReadInstructions(entrypoint);
    var calls = Calls(instructions).ToArray();
    var build = calls.Where(call => call.Method.Name == "Build" &&
        call.Method.DeclaringType?.FullName == "Microsoft.Maui.Hosting.MauiAppBuilder")
        .ToArray();
    Require(build.Length == 1, "The clean entrypoint must build exactly one MAUI application.");
    Require(IsReachable(instructions, build[0].Offset),
        "The compiled MAUI Build call is unreachable.");
    Require(calls.Any(call => call.Method.Name == "CreateBuilder" &&
        call.Method.DeclaringType?.FullName == "Microsoft.Maui.Hosting.MauiApp" &&
        call.Offset < build[0].Offset &&
        Dominates(instructions, call.Offset, build[0].Offset)),
        "The clean entrypoint does not create its MAUI builder before Build.");
    Require(calls.Any(call => call.Method is MethodInfo method &&
        method.Name == "UseMauiApp" && method.IsGenericMethod &&
        method.GetGenericArguments().SingleOrDefault() == app &&
        call.Offset < build[0].Offset &&
        Dominates(instructions, call.Offset, build[0].Offset)),
        "The clean entrypoint does not bind the compiled App before Build.");

    var requiredRegistrations = new[]
    {
        "Deep.Client.Maui.Services.DeepAccountRuntimeAccessor",
        "Deep.Client.Maui.Services.DeepContactResolveRuntimeAccessor",
        "Deep.Client.Maui.Services.AccountOwnedContactRouteAdvertisementAuthor",
        "Deep.Client.Maui.Core.Services.IDeepAccountDirectoryAdmissionCoordinator",
        "Deep.Client.Maui.Services.IProductionContactResolveVerifiedHostCapabilitiesSource",
        "Deep.Client.Maui.AppShell"
    };
    foreach (var typeName in requiredRegistrations)
    {
        Require(calls.Any(call => call.Offset < build[0].Offset &&
            Dominates(instructions, call.Offset, build[0].Offset) &&
            call.Method is MethodInfo method &&
            method.Name is "AddSingleton" or "TryAddSingleton" &&
            method.IsGenericMethod &&
            method.GetGenericArguments().Any(type => type.FullName == typeName)),
            $"The compiled clean composition does not register {typeName} before Build.");
    }
    Require(calls.Any(call => call.Offset < build[0].Offset &&
        Dominates(instructions, call.Offset, build[0].Offset) &&
        call.Method.Name == "AddProductionContactResolveRuntimePrerequisites"),
        "The compiled contact authority prerequisites are missing.");
    Require(calls.Any(call => call.Offset < build[0].Offset &&
        Dominates(instructions, call.Offset, build[0].Offset) &&
        call.Method.Name == "Resolve" &&
        call.Method.DeclaringType?.Name == "RealityTransportBindingResolver"),
        "The compiled privacy-routed transport binding is missing.");
    Require(calls.Any(call => call.Offset < build[0].Offset &&
        Dominates(instructions, call.Offset, build[0].Offset) &&
        call.Method.Name == "get_Production" &&
        call.Method.DeclaringType?.Name == "HttpServiceEndpointPolicy"),
        "The production HTTP endpoint policy is missing.");
    Require(calls.Any(call => call.Offset < build[0].Offset &&
        Dominates(instructions, call.Offset, build[0].Offset) &&
        call.Method.Name == "get_Production" &&
        call.Method.DeclaringType?.Name == "RoutedRuntimeEndpointPolicy"),
        "The production routed endpoint policy is missing.");
    Require(calls.Where(call => call.Offset > build[0].Offset)
        .All(call => call.Method.Name is not ("AddSingleton" or "TryAddSingleton" or
            "AddTransient" or "AddProductionContactResolveRuntimePrerequisites")),
        "The compiled entrypoint mutates application services after Build.");

    var settings = program.GetMethod("ResolveRuntimeSetting",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The release runtime-settings reader is missing.");
    Require(ReadCalls(settings).All(call =>
        !(call.Method.Name == "GetEnvironmentVariable" &&
          call.Method.DeclaringType == typeof(Environment))),
        "Release runtime settings can read mutable process environment variables.");

    var forbiddenTypes = new[]
    {
        "Deep.Client.Maui.Services.ClientRuntimeBootstrapper",
        "Deep.Client.Maui.Services.PersistentClientRuntimeComposer",
        "Deep.Client.Maui.Services.CallSessionCoordinator",
        "Deep.Client.Maui.Pages.ChatPage"
    };
    foreach (var typeName in forbiddenTypes)
        Require(assembly.GetType(typeName, throwOnError: false) is null,
            $"The retired Session-era type {typeName} remains in the release assembly.");

    Console.WriteLine($"Compiled clean MAUI release composition verified ({requiredRegistrations.Length} critical registrations).");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static Type RequiredType(Assembly assembly, string name) =>
    assembly.GetType(name, throwOnError: false)
    ?? throw new InvalidOperationException($"Compiled type {name} is missing.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertControlFlowFixtures()
{
    foreach (var (methodName, expected) in new[]
             {
                 (nameof(ControlFlowFixtures.Unconditional), true),
                 (nameof(ControlFlowFixtures.Conditional), false)
             })
    {
        var method = typeof(ControlFlowFixtures).GetMethod(methodName,
            BindingFlags.Public | BindingFlags.Static)!;
        var instructions = ReadInstructions(method);
        var calls = Calls(instructions).ToArray();
        var required = calls.Single(call =>
            call.Method.Name == nameof(ControlFlowFixtures.Required));
        var build = calls.Single(call =>
            call.Method.Name == nameof(ControlFlowFixtures.Build));
        Require(Dominates(instructions, required.Offset, build.Offset) == expected,
            "The compiled control-flow dominance verifier failed its negative fixture.");
    }
}

static IEnumerable<CompiledCall> ReadCalls(MethodBase method) =>
    Calls(ReadInstructions(method));

static IEnumerable<CompiledCall> Calls(IReadOnlyList<CompiledInstruction> instructions) =>
    instructions.Where(instruction => instruction.Method is not null)
        .Select(instruction => new CompiledCall(
            instruction.Offset, instruction.Method!));

static bool IsReachable(IReadOnlyList<CompiledInstruction> instructions,
    int target) => Walk(instructions, target, excluded: null);

static bool Dominates(IReadOnlyList<CompiledInstruction> instructions,
    int dominator, int target) =>
    IsReachable(instructions, dominator) &&
    !Walk(instructions, target, excluded: dominator);

static bool Walk(IReadOnlyList<CompiledInstruction> instructions,
    int target, int? excluded)
{
    var byOffset = instructions.ToDictionary(instruction => instruction.Offset);
    var pending = new Stack<int>();
    var visited = new HashSet<int>();
    pending.Push(instructions[0].Offset);
    while (pending.Count > 0)
    {
        var offset = pending.Pop();
        if (offset == excluded || !visited.Add(offset)) continue;
        if (offset == target) return true;
        var instruction = byOffset[offset];
        if (instruction.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw)
            continue;
        foreach (var successor in instruction.BranchTargets)
        {
            Require(byOffset.ContainsKey(successor),
                "The compiled IL contains an invalid branch target.");
            pending.Push(successor);
        }
        if (instruction.OpCode.FlowControl != FlowControl.Branch &&
            byOffset.ContainsKey(instruction.NextOffset))
            pending.Push(instruction.NextOffset);
    }
    return false;
}

static IReadOnlyList<CompiledInstruction> ReadInstructions(MethodBase method)
{
    var body = method.GetMethodBody()
        ?? throw new InvalidOperationException($"{method.Name} has no compiled IL body.");
    Require(body.ExceptionHandlingClauses.Count == 0,
        $"{method.Name} contains an unmodelled exception-control-flow edge.");
    var bytes = body.GetILAsByteArray()
        ?? throw new InvalidOperationException($"{method.Name} has no compiled IL bytes.");
    var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opcode => unchecked((ushort)opcode.Value));
    var typeArguments = method.DeclaringType?.GetGenericArguments();
    var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
    var instructions = new List<CompiledInstruction>();
    for (var offset = 0; offset < bytes.Length;)
    {
        var instructionOffset = offset;
        ushort value = bytes[offset++];
        if (value == 0xfe)
        {
            if (offset == bytes.Length)
                throw new InvalidOperationException("Truncated compiled IL opcode.");
            value = (ushort)(0xfe00 | bytes[offset++]);
        }
        if (!opcodes.TryGetValue(value, out var opcode))
            throw new InvalidOperationException("Unknown compiled IL opcode.");
        var size = opcode.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or
                OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or
                OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
                OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch when offset <= bytes.Length - 4 =>
                checked(4 + 4 * BitConverter.ToInt32(bytes, offset)),
            _ => throw new InvalidOperationException("Unsupported compiled IL operand.")
        };
        if (size < 0 || offset > bytes.Length - size)
            throw new InvalidOperationException("Truncated compiled IL operand.");
        MethodBase? called = null;
        if (opcode.OperandType == OperandType.InlineMethod &&
            (opcode == OpCodes.Call || opcode == OpCodes.Callvirt ||
             opcode == OpCodes.Newobj))
            called = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, offset),
                typeArguments, methodArguments)
                ?? throw new InvalidOperationException("Unresolvable compiled IL call.");
        int[] branches = [];
        if (opcode.OperandType == OperandType.ShortInlineBrTarget)
            branches = [offset + 1 + unchecked((sbyte)bytes[offset])];
        else if (opcode.OperandType == OperandType.InlineBrTarget)
            branches = [offset + 4 + BitConverter.ToInt32(bytes, offset)];
        else if (opcode.OperandType == OperandType.InlineSwitch)
        {
            var count = BitConverter.ToInt32(bytes, offset);
            Require(count >= 0, "A compiled IL switch count is negative.");
            var switchBase = offset + size;
            branches = Enumerable.Range(0, count)
                .Select(index => switchBase +
                    BitConverter.ToInt32(bytes, offset + 4 + 4 * index))
                .ToArray();
        }
        offset += size;
        instructions.Add(new CompiledInstruction(instructionOffset, offset,
            opcode, called, branches));
    }
    var validOffsets = instructions.Select(instruction => instruction.Offset).ToHashSet();
    Require(instructions.All(instruction =>
        instruction.BranchTargets.All(validOffsets.Contains)),
        "The compiled IL contains an invalid branch target.");
    return instructions;
}

internal readonly record struct CompiledCall(int Offset, MethodBase Method);
internal readonly record struct CompiledInstruction(int Offset, int NextOffset,
    OpCode OpCode, MethodBase? Method, IReadOnlyList<int> BranchTargets);

internal static class ControlFlowFixtures
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Required() { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Build() { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Unconditional(bool condition)
    {
        Required();
        if (condition) _ = Environment.TickCount;
        Build();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Conditional(bool condition)
    {
        if (condition) Required();
        Build();
    }
}
