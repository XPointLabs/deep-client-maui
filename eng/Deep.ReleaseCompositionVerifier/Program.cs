using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Reflection.Emit;

const string routerOne = "1111111111111111111111111111111111111111111111111111111111111111";
const string routerTwo = "2222222222222222222222222222222222222222222222222222222222222222";
const string routerThree = "3333333333333333333333333333333333333333333333333333333333333333";
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
    var createMauiApp = mauiProgram.GetMethod(
        "CreateMauiApp",
        BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("MauiProgram.CreateMauiApp was not found.");
    var configureProduction = mauiProgram.GetMethod(
        "ConfigureProductionRoutedServices",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "MauiProgram.ConfigureProductionRoutedServices was not found.");
    Require(
        CountCalls(createMauiApp, configureProduction) == 1,
        "Compiled CreateMauiApp must contain exactly one call to the production routed registrar.");

    var services = new ServiceCollection();
    using var routerHttpClient = new HttpClient();
    var composition = configureProduction.Invoke(
        null,
        [services, pinnedRouters, null, routerHttpClient]) as RoutedProductionComposition
        ?? throw new InvalidOperationException("Production registrar returned no routed composition.");
    using var provider = services.BuildServiceProvider();
    ValidateProductionProvider(provider, pinnedRouters, composition);
    AssertMutationIsRejected(pinnedRouters);
    Console.WriteLine("Compiled MauiProgram Release DI registration path verified.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static void ValidateProductionProvider(
    IServiceProvider services,
    IReadOnlyList<PinnedRouterEndpoint> expectedRouters,
    RoutedProductionComposition expectedComposition)
{
    var routeProviders = services.GetServices<ITransportRouteProvider>().ToArray();
    var messageTransports = services.GetServices<ISessionMessageTransport>().ToArray();
    var routers = services.GetServices<XNodeRpcClient>().ToArray();
    var compositions = services.GetServices<RoutedProductionComposition>().ToArray();

    Require(routeProviders.Length == 1, "Expected exactly one ITransportRouteProvider.");
    Require(messageTransports.Length == 1, "Expected exactly one ISessionMessageTransport.");
    Require(routers.Length == 1, "Expected exactly one XNodeRpcClient.");
    Require(compositions.Length == 1, "Expected exactly one routed production composition.");
    Require(ReferenceEquals(compositions[0], expectedComposition), "Unexpected DI composition instance.");
    Require(
        routeProviders[0].GetType() == typeof(XNodeRpcClient),
        "Production route provider is not XNodeRpcClient.");
    Require(
        messageTransports[0].GetType() == typeof(RoutedSessionStorageMessageTransport),
        "Production message transport is not RoutedSessionStorageMessageTransport.");
    Require(ReferenceEquals(routeProviders[0], routers[0]), "Route provider and XNode client differ.");
    Require(ReferenceEquals(compositions[0].Router, routers[0]), "Factory router is not the DI router.");
    Require(
        ReferenceEquals(compositions[0].RouteProvider, routeProviders[0]),
        "Factory route provider is not the DI route provider.");
    Require(
        ReferenceEquals(compositions[0].SessionMessageTransport, messageTransports[0]),
        "Factory message transport is not the DI message transport.");
    Require(
        compositions[0].PinnedRouters.SequenceEqual(expectedRouters),
        "Production composition does not contain the exact three expected pins.");
    Require(
        services.GetService<DirectStorageRouteProvider>() is null,
        "DirectStorageRouteProvider concrete service is present.");
    Require(
        services.GetService<SessionStorageMessageTransport>() is null,
        "Direct SessionStorageMessageTransport concrete service is present.");
    Require(
        services.GetService<StubSessionBackend>() is null,
        "StubSessionBackend concrete service is present.");
    Require(
        routeProviders.All(static provider => provider is not DirectStorageRouteProvider),
        "Direct storage route provider is bound to the production interface.");
    Require(
        messageTransports.All(static transport =>
            transport is not SessionStorageMessageTransport and not StubSessionBackend),
        "Direct or stub message transport is bound to the production interface.");
}

static void AssertMutationIsRejected(IReadOnlyList<PinnedRouterEndpoint> pinnedRouters)
{
    using var deadFactoryHttpClient = new HttpClient();
    var deadFactoryOutput = RoutedProductionCompositionFactory.Create(
        pinnedRouters,
        directStorageUrl: null,
        deadFactoryHttpClient);
    var mutated = new ServiceCollection();
    mutated.AddSingleton(deadFactoryOutput);
    mutated.AddSingleton(deadFactoryOutput.Router);
    mutated.AddSingleton<ITransportRouteProvider>(
        new DirectStorageRouteProvider("https://storage.invalid/"));
    mutated.AddSingleton<ISessionMessageTransport>(new StubSessionBackend());
    using var provider = mutated.BuildServiceProvider();

    try
    {
        ValidateProductionProvider(provider, pinnedRouters, deadFactoryOutput);
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new InvalidOperationException(
        "Negative DI mutation was accepted despite dead factory calls and direct/stub bindings.");
}

static int CountCalls(MethodInfo source, MethodInfo target)
{
    var il = source.GetMethodBody()?.GetILAsByteArray()
        ?? throw new InvalidOperationException($"{source.Name} has no executable IL.");
    var opcodeMap = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static field => field.FieldType == typeof(OpCode))
        .Select(static field => (OpCode)field.GetValue(null)!)
        .ToDictionary(static opcode => unchecked((ushort)opcode.Value));
    var count = 0;
    var offset = 0;
    while (offset < il.Length)
    {
        var opcodeValue = ReadOpcode(il, ref offset);
        if (!opcodeMap.TryGetValue(opcodeValue, out var opcode))
        {
            throw new InvalidOperationException($"Unknown IL opcode 0x{opcodeValue:x4}.");
        }
        if (opcode.OperandType == OperandType.InlineMethod)
        {
            EnsureAvailable(il, offset, sizeof(int));
            var token = BitConverter.ToInt32(il, offset);
            offset += sizeof(int);
            var called = source.Module.ResolveMethod(token);
            if (called is not null &&
                called.Module == target.Module &&
                called.MetadataToken == target.MetadataToken)
            {
                count++;
            }
            continue;
        }

        offset += OperandSize(opcode.OperandType, il, offset);
    }

    return count;
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

static int OperandSize(OperandType operandType, byte[] il, int offset)
{
    var size = operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineI or OperandType.ShortInlineBrTarget or
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineString or
        OperandType.InlineType or OperandType.InlineField or OperandType.InlineMethod or
        OperandType.InlineSig or OperandType.InlineTok or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => SwitchSize(il, offset),
        _ => throw new InvalidOperationException($"Unsupported IL operand type: {operandType}")
    };
    EnsureAvailable(il, offset, size);
    return size;
}

static int SwitchSize(byte[] il, int offset)
{
    EnsureAvailable(il, offset, sizeof(int));
    var count = BitConverter.ToInt32(il, offset);
    Require(count >= 0, "Invalid switch operand.");
    return checked(sizeof(int) + (count * sizeof(int)));
}

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
