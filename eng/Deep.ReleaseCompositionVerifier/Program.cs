using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

const string routerOne = "1111111111111111111111111111111111111111111111111111111111111111";
const string routerTwo = "2222222222222222222222222222222222222222222222222222222222222222";
const string routerThree = "3333333333333333333333333333333333333333333333333333333333333333";
const string expectedDescriptorFingerprint =
    "d30e6bb8ccce63459fa4b8eb4061ed285ea5ad9812db259fef31d2d5e37321fe";
var pinnedRouters = new[]
{
    new PinnedRouterEndpoint("http://127.0.0.1:29281/", routerOne),
    new PinnedRouterEndpoint("http://127.0.0.1:29282/", routerTwo),
    new PinnedRouterEndpoint("http://127.0.0.1:29283/", routerThree)
};

try
{
    if (args.Length != 1 || !File.Exists(args[0]))
    {
        throw new InvalidOperationException(
            "Usage: Deep.ReleaseCompositionVerifier <compiled Deep.Client.Maui.dll>");
    }

    var appAssembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
    var mauiProgram = appAssembly.GetType("Deep.Client.Maui.MauiProgram", throwOnError: true)!;
    var createMauiApp = RequiredMethod(
        mauiProgram,
        "CreateMauiApp",
        BindingFlags.Public | BindingFlags.Static);
    var configureApplicationServices = RequiredMethod(
        mauiProgram,
        "ConfigureApplicationServices",
        BindingFlags.NonPublic | BindingFlags.Static);

    VerifyCompiledCompositionControlFlow(createMauiApp, configureApplicationServices);
    AssertCompiledNegativeFixturesAreRejected();

    var services = new ServiceCollection();
    using var routerHttpClient = new HttpClient();
    configureApplicationServices.Invoke(
        null,
        [services, pinnedRouters, null, routerHttpClient]);
    using var provider = services.BuildServiceProvider(
        new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = true
        });
    ValidateFinalApplicationComposition(
        services,
        provider,
        pinnedRouters,
        expectedDescriptorFingerprint);
    AssertPostEntrypointMutationIsRejected(
        configureApplicationServices,
        pinnedRouters);

    Console.WriteLine(
        $"Compiled final application DI composition verified ({services.Count} app-owned descriptors).");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static MethodInfo RequiredMethod(Type type, string name, BindingFlags flags) =>
    type.GetMethod(name, flags)
    ?? throw new InvalidOperationException($"{type.FullName}.{name} was not found.");

static void ValidateFinalApplicationComposition(
    IServiceCollection descriptors,
    IServiceProvider provider,
    IReadOnlyList<PinnedRouterEndpoint> expectedRouters,
    string expectedFingerprint)
{
    Require(descriptors.Count == 65, "Final Windows Release app-owned descriptor count changed.");
    Require(
        DescriptorFingerprint(descriptors) == expectedFingerprint,
        "Final Windows Release app-owned descriptor manifest changed.");
    var routeDescriptors = descriptors
        .Where(static descriptor => descriptor.ServiceType == typeof(ITransportRouteProvider))
        .ToArray();
    var messageDescriptors = descriptors
        .Where(static descriptor => descriptor.ServiceType == typeof(ISessionMessageTransport))
        .ToArray();
    var routerDescriptors = descriptors
        .Where(static descriptor => descriptor.ServiceType == typeof(XNodeRpcClient))
        .ToArray();
    var compositionDescriptors = descriptors
        .Where(static descriptor => descriptor.ServiceType == typeof(RoutedProductionComposition))
        .ToArray();

    Require(routeDescriptors.Length == 1, "Expected one final route-provider descriptor.");
    Require(messageDescriptors.Length == 1, "Expected one final message-transport descriptor.");
    Require(routerDescriptors.Length == 1, "Expected one final XNode client descriptor.");
    Require(compositionDescriptors.Length == 1, "Expected one final routed-composition descriptor.");
    Require(
        routeDescriptors[0].ImplementationInstance?.GetType() == typeof(XNodeRpcClient),
        "Final route descriptor is not the factory XNode instance.");
    Require(
        messageDescriptors[0].ImplementationInstance?.GetType() ==
            typeof(RoutedSessionStorageMessageTransport),
        "Final message descriptor is not the routed storage transport instance.");

    var routeProviders = provider.GetServices<ITransportRouteProvider>().ToArray();
    var messageTransports = provider.GetServices<ISessionMessageTransport>().ToArray();
    var routers = provider.GetServices<XNodeRpcClient>().ToArray();
    var compositions = provider.GetServices<RoutedProductionComposition>().ToArray();
    Require(routeProviders.Length == 1, "Provider resolved multiple route providers.");
    Require(messageTransports.Length == 1, "Provider resolved multiple message transports.");
    Require(routers.Length == 1, "Provider resolved multiple XNode clients.");
    Require(compositions.Length == 1, "Provider resolved multiple routed compositions.");
    Require(ReferenceEquals(routeProviders[0], routers[0]), "Route provider and XNode client differ.");
    Require(ReferenceEquals(compositions[0].Router, routers[0]), "Factory router is not the DI router.");
    Require(
        ReferenceEquals(compositions[0].RouteProvider, routeProviders[0]),
        "Factory route provider is not the final DI route provider.");
    Require(
        ReferenceEquals(compositions[0].SessionMessageTransport, messageTransports[0]),
        "Factory message transport is not the final DI transport.");
    Require(
        compositions[0].PinnedRouters.SequenceEqual(expectedRouters),
        "Final composition does not contain the exact three expected pins.");

    var forbidden = new[]
    {
        typeof(DirectStorageRouteProvider),
        typeof(SessionStorageMessageTransport),
        typeof(StubSessionBackend)
    };
    var forbiddenDescriptor = descriptors.FirstOrDefault(descriptor =>
        forbidden.Contains(descriptor.ServiceType) ||
        (descriptor.ImplementationType is not null &&
         forbidden.Contains(descriptor.ImplementationType)) ||
        (descriptor.ImplementationInstance is not null &&
         forbidden.Contains(descriptor.ImplementationInstance.GetType())));
    Require(forbiddenDescriptor is null, "Direct/stub concrete descriptor is present.");
    foreach (var forbiddenType in forbidden)
    {
        Require(
            provider.GetService(forbiddenType) is null,
            $"Forbidden concrete service resolves: {forbiddenType.Name}.");
    }
}

static string DescriptorFingerprint(IServiceCollection descriptors)
{
    var canonical = string.Join(
        '\n',
        descriptors.Select((descriptor, index) =>
        {
            var implementation = descriptor.ImplementationType?.FullName ??
                descriptor.ImplementationInstance?.GetType().FullName ??
                (descriptor.ImplementationFactory is null ? "<missing>" : "<factory>");
            return $"{index}|{descriptor.ServiceType.FullName}|{descriptor.Lifetime}|{implementation}";
        }));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
        .ToLowerInvariant();
}

static void AssertPostEntrypointMutationIsRejected(
    MethodInfo configureApplicationServices,
    IReadOnlyList<PinnedRouterEndpoint> expectedRouters)
{
    var mutated = new ServiceCollection();
    using var routerHttpClient = new HttpClient();
    configureApplicationServices.Invoke(
        null,
        [mutated, expectedRouters, null, routerHttpClient]);
    mutated.AddSingleton<ITransportRouteProvider>(
        new DirectStorageRouteProvider("https://storage.invalid/"));
    mutated.AddSingleton<ISessionMessageTransport>(new StubSessionBackend());
    mutated.TryAddSingleton(new SessionStorageMessageTransport(
        new HttpClient(),
        new SessionStorageMessageTransportOptions("https://storage.invalid/")));
    using var provider = mutated.BuildServiceProvider();

    try
    {
        ValidateFinalApplicationComposition(
            mutated,
            provider,
            expectedRouters,
            expectedFingerprint: string.Empty);
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new InvalidOperationException(
        "Post-entrypoint direct/stub descriptor mutation was accepted.");
}

static void VerifyCompiledCompositionControlFlow(MethodInfo source, MethodInfo entrypoint)
{
    var instructions = ReadInstructions(source);
    var entryCalls = CallsTo(instructions, entrypoint).ToArray();
    var buildCalls = instructions
        .Where(static instruction =>
            instruction.CalledMethod?.Name == "Build" &&
            instruction.CalledMethod.DeclaringType?.FullName ==
                "Microsoft.Maui.Hosting.MauiAppBuilder")
        .ToArray();
    var returns = instructions
        .Where(static instruction => instruction.OpCode.FlowControl == FlowControl.Return)
        .ToArray();

    Require(entryCalls.Length == 1, "CreateMauiApp must call the app DI entrypoint exactly once.");
    Require(buildCalls.Length == 1, "CreateMauiApp must call MauiAppBuilder.Build exactly once.");
    Require(returns.Length == 1, "CreateMauiApp must have one successful return path.");
    var entryCall = entryCalls[0];
    var buildCall = buildCalls[0];
    Require(IsReachable(instructions, entryCall.Offset), "App DI entrypoint is compiled but unreachable.");
    Require(IsReachable(instructions, buildCall.Offset), "MauiAppBuilder.Build is unreachable.");
    Require(IsReachable(instructions, returns[0].Offset), "CreateMauiApp return is unreachable.");
    Require(
        instructions.All(static instruction => !IsDirectServiceRegistrationCall(instruction)),
        "CreateMauiApp contains an app-owned DI registration outside the headless entrypoint.");
    Require(entryCall.Offset < buildCall.Offset, "App DI entrypoint must precede Build.");
    Require(
        Dominates(instructions, entryCall.Offset, buildCall.Offset),
        "App DI entrypoint does not dominate Build.");
    Require(
        Dominates(instructions, entryCall.Offset, returns[0].Offset),
        "App DI entrypoint does not dominate every return.");
    Require(
        Dominates(instructions, buildCall.Offset, returns[0].Offset),
        "Build does not dominate the successful return.");

    var between = instructions
        .Where(instruction =>
            instruction.Offset > entryCall.Offset &&
            instruction.Offset < buildCall.Offset)
        .ToArray();
    Require(
        between.All(static instruction =>
            instruction.OpCode.FlowControl is not (
                FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Call)),
        "Control flow or helper calls exist between app DI composition and Build.");
    Require(
        between.Length <= 2,
        "App DI composition is not immediately adjacent to Build.");
    var afterBuild = instructions
        .Where(instruction =>
            instruction.Offset > buildCall.Offset &&
            instruction.Offset < returns[0].Offset)
        .ToArray();
    Require(
        afterBuild.All(static instruction => instruction.OpCode.FlowControl != FlowControl.Call),
        "A registration/helper call exists after Build and before return.");
}

static void AssertCompiledNegativeFixturesAreRejected()
{
    var marker = RequiredMethod(
        typeof(CompiledGuardFixtures),
        nameof(CompiledGuardFixtures.ApplicationEntrypointMarker),
        BindingFlags.Public | BindingFlags.Static);
    var build = RequiredMethod(
        typeof(CompiledGuardFixtures),
        nameof(CompiledGuardFixtures.BuildMarker),
        BindingFlags.Public | BindingFlags.Static);
    AssertControlFlowRejected(
        RequiredMethod(
            typeof(CompiledGuardFixtures),
            nameof(CompiledGuardFixtures.ConditionalEntrypoint),
            BindingFlags.Public | BindingFlags.Static),
        marker,
        build);
    AssertControlFlowRejected(
        RequiredMethod(
            typeof(CompiledGuardFixtures),
            nameof(CompiledGuardFixtures.DeadEntrypoint),
            BindingFlags.Public | BindingFlags.Static),
        marker,
        build);
    AssertControlFlowRejected(
        RequiredMethod(
            typeof(CompiledGuardFixtures),
            nameof(CompiledGuardFixtures.IndirectRegistrationAfterEntrypoint),
            BindingFlags.Public | BindingFlags.Static),
        marker,
        build);
}

static void AssertControlFlowRejected(MethodInfo source, MethodInfo entrypoint, MethodInfo build)
{
    try
    {
        VerifyGenericControlFlow(source, entrypoint, build);
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new InvalidOperationException(
        $"Negative compiled fixture was accepted: {source.Name}.");
}

static void VerifyGenericControlFlow(MethodInfo source, MethodInfo entrypoint, MethodInfo build)
{
    var instructions = ReadInstructions(source);
    var entryCalls = CallsTo(instructions, entrypoint).ToArray();
    var buildCalls = CallsTo(instructions, build).ToArray();
    var returns = instructions
        .Where(static instruction => instruction.OpCode.FlowControl == FlowControl.Return)
        .ToArray();
    Require(entryCalls.Length == 1 && buildCalls.Length == 1 && returns.Length == 1, "Invalid call shape.");
    Require(IsReachable(instructions, entryCalls[0].Offset), "Entrypoint is unreachable/dead.");
    Require(IsReachable(instructions, buildCalls[0].Offset), "Build is unreachable.");
    Require(Dominates(instructions, entryCalls[0].Offset, buildCalls[0].Offset), "Entrypoint is conditional/dead.");
    var between = instructions.Where(instruction =>
        instruction.Offset > entryCalls[0].Offset &&
        instruction.Offset < buildCalls[0].Offset);
    Require(
        between.All(static instruction => instruction.OpCode.FlowControl != FlowControl.Call),
        "Indirect registration call exists after entrypoint.");
}

static IEnumerable<IlInstruction> CallsTo(
    IReadOnlyList<IlInstruction> instructions,
    MethodInfo target) =>
    instructions.Where(instruction =>
        instruction.CalledMethod is not null &&
        instruction.CalledMethod.Module == target.Module &&
        instruction.CalledMethod.MetadataToken == target.MetadataToken);

static bool IsDirectServiceRegistrationCall(IlInstruction instruction)
{
    var called = instruction.CalledMethod;
    if (called is null)
    {
        return false;
    }
    var declaringNamespace = called.DeclaringType?.Namespace ?? string.Empty;
    return declaringNamespace.StartsWith(
            "Microsoft.Extensions.DependencyInjection",
            StringComparison.Ordinal) &&
        (called.Name.StartsWith("Add", StringComparison.Ordinal) ||
         called.Name.StartsWith("TryAdd", StringComparison.Ordinal) ||
         called.Name == "Replace");
}

static bool IsReachable(IReadOnlyList<IlInstruction> instructions, int targetOffset)
{
    var byOffset = instructions.ToDictionary(static instruction => instruction.Offset);
    var visited = new HashSet<int>();
    var pending = new Stack<int>();
    pending.Push(instructions[0].Offset);
    while (pending.Count > 0)
    {
        var offset = pending.Pop();
        if (!visited.Add(offset))
        {
            continue;
        }
        if (offset == targetOffset)
        {
            return true;
        }
        foreach (var successor in Successors(byOffset[offset], byOffset))
        {
            pending.Push(successor);
        }
    }
    return false;
}

static bool Dominates(
    IReadOnlyList<IlInstruction> instructions,
    int dominatorOffset,
    int targetOffset)
{
    var byOffset = instructions.ToDictionary(static instruction => instruction.Offset);
    var visited = new HashSet<int>();
    var pending = new Stack<int>();
    pending.Push(instructions[0].Offset);
    while (pending.Count > 0)
    {
        var offset = pending.Pop();
        if (offset == dominatorOffset || !visited.Add(offset))
        {
            continue;
        }
        if (offset == targetOffset)
        {
            return false;
        }
        foreach (var successor in Successors(byOffset[offset], byOffset))
        {
            pending.Push(successor);
        }
    }

    return true;
}

static IEnumerable<int> Successors(
    IlInstruction instruction,
    IReadOnlyDictionary<int, IlInstruction> byOffset)
{
    if (instruction.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw)
    {
        yield break;
    }
    foreach (var target in instruction.BranchTargets)
    {
        if (byOffset.ContainsKey(target))
        {
            yield return target;
        }
    }
    if (instruction.OpCode.FlowControl != FlowControl.Branch &&
        byOffset.ContainsKey(instruction.NextOffset))
    {
        yield return instruction.NextOffset;
    }
}

static IReadOnlyList<IlInstruction> ReadInstructions(MethodInfo method)
{
    var il = method.GetMethodBody()?.GetILAsByteArray()
        ?? throw new InvalidOperationException($"{method.Name} has no executable IL.");
    var opcodeMap = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static field => field.FieldType == typeof(OpCode))
        .Select(static field => (OpCode)field.GetValue(null)!)
        .ToDictionary(static opcode => unchecked((ushort)opcode.Value));
    var instructions = new List<IlInstruction>();
    var offset = 0;
    while (offset < il.Length)
    {
        var start = offset;
        var opcodeValue = ReadOpcode(il, ref offset);
        if (!opcodeMap.TryGetValue(opcodeValue, out var opcode))
        {
            throw new InvalidOperationException($"Unknown IL opcode 0x{opcodeValue:x4}.");
        }
        MethodBase? calledMethod = null;
        int[] branchTargets = [];
        switch (opcode.OperandType)
        {
            case OperandType.InlineMethod:
                EnsureAvailable(il, offset, sizeof(int));
                calledMethod = method.Module.ResolveMethod(BitConverter.ToInt32(il, offset));
                offset += sizeof(int);
                break;
            case OperandType.ShortInlineBrTarget:
                EnsureAvailable(il, offset, 1);
                var shortDelta = unchecked((sbyte)il[offset]);
                offset += 1;
                branchTargets = [offset + shortDelta];
                break;
            case OperandType.InlineBrTarget:
                EnsureAvailable(il, offset, sizeof(int));
                var delta = BitConverter.ToInt32(il, offset);
                offset += sizeof(int);
                branchTargets = [offset + delta];
                break;
            case OperandType.InlineSwitch:
                EnsureAvailable(il, offset, sizeof(int));
                var count = BitConverter.ToInt32(il, offset);
                Require(count >= 0, "Invalid switch operand.");
                var switchBase = checked(offset + sizeof(int) + (count * sizeof(int)));
                EnsureAvailable(il, offset, checked(sizeof(int) + (count * sizeof(int))));
                branchTargets = Enumerable.Range(0, count)
                    .Select(index =>
                        switchBase + BitConverter.ToInt32(
                            il,
                            offset + sizeof(int) + (index * sizeof(int))))
                    .ToArray();
                offset = switchBase;
                break;
            default:
                offset += OperandSize(opcode.OperandType);
                EnsureAvailable(il, offset, 0);
                break;
        }
        instructions.Add(new IlInstruction(start, offset, opcode, calledMethod, branchTargets));
    }

    return instructions;
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

static int OperandSize(OperandType operandType) => operandType switch
{
    OperandType.InlineNone => 0,
    OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
    OperandType.InlineVar => 2,
    OperandType.InlineI or OperandType.InlineString or OperandType.InlineType or
    OperandType.InlineField or OperandType.InlineSig or OperandType.InlineTok or
    OperandType.ShortInlineR => 4,
    OperandType.InlineI8 or OperandType.InlineR => 8,
    _ => throw new InvalidOperationException($"Unsupported IL operand type: {operandType}")
};

static void EnsureAvailable(byte[] il, int offset, int size)
{
    if (offset < 0 || size < 0 || offset > il.Length - size)
    {
        throw new InvalidOperationException("Invalid or truncated IL stream.");
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record IlInstruction(
    int Offset,
    int NextOffset,
    OpCode OpCode,
    MethodBase? CalledMethod,
    IReadOnlyList<int> BranchTargets);

static class CompiledGuardFixtures
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ApplicationEntrypointMarker()
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object BuildMarker() => new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object ConditionalEntrypoint(bool execute)
    {
        if (execute)
        {
            ApplicationEntrypointMarker();
        }
        return BuildMarker();
    }

#pragma warning disable CS0162
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object DeadEntrypoint()
    {
        goto Build;
        ApplicationEntrypointMarker();
    Build:
        return BuildMarker();
    }
#pragma warning restore CS0162

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object IndirectRegistrationAfterEntrypoint()
    {
        ApplicationEntrypointMarker();
        IndirectRegistrationHelper();
        return BuildMarker();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IndirectRegistrationHelper()
    {
    }
}
