using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Linq.Expressions;

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
    VerifyCreateMauiAppCallAllowlist(createMauiApp, configureApplicationServices);
    VerifyDeterministicCompositionEntrypoint(configureApplicationServices);
    VerifyNoReachableForbiddenTransportTokens(createMauiApp);
    AssertCompiledNegativeFixturesAreRejected();
    AssertNameBoundConstructorBindingIsOrderIndependent();

    using var routerHttpClient = new HttpClient();
    var inputs = CreateSyntheticInputs(
        appAssembly,
        pinnedRouters,
        routerHttpClient);
    var services = new ServiceCollection();
    configureApplicationServices.Invoke(null, [services, inputs]);
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
        inputs,
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

static object CreateSyntheticInputs(
    Assembly appAssembly,
    IReadOnlyList<PinnedRouterEndpoint> pinnedRouters,
    HttpClient routerHttpClient)
{
    var runtimeType = appAssembly.GetType(
        "Deep.Client.Maui.Services.RuntimeEnvironmentOptions",
        throwOnError: true)!;
    var runtimeConstructor = runtimeType.GetConstructors().Single();
    var runtime = runtimeConstructor.Invoke(BindNamedArguments(
        runtimeConstructor,
        new Dictionary<string, Func<Type, object?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["StorageUrl"] = _ => null,
            ["TransportUrl"] = _ => null,
            ["RouterUrls"] = _ => null,
            ["FileUrl"] = _ => "https://files.example/",
            ["PushUrl"] = _ => "https://push.example/",
            ["CallSignalingUrl"] = _ => "https://calls.example/",
            ["RegistryUrl"] = _ => null,
            ["StakingBackendUrl"] = _ => null,
            ["StakingPortalUrl"] = _ => null
        }));
    var inputsType = appAssembly.GetType(
        "Deep.Client.Maui.ApplicationServiceInputs",
        throwOnError: true)!;
    var constructor = inputsType.GetConstructors().Single();
    var transportFactory = new HttpServiceTransportFactory(
        HttpServiceEndpointPolicy.Production);
    var clientOptions = new HttpServiceClientOptions();
    Func<IServiceProvider, IAvatarProfileTransport> avatarFactory =
        _ => transportFactory.CreateAvatar(
            new HttpAvatarProfileTransportOptions("https://files.example/"),
            clientOptions);
    Func<IServiceProvider, IAttachmentFileTransport> attachmentFactory =
        _ => transportFactory.CreateAttachment(
            new HttpAttachmentFileTransportOptions("https://files.example/"),
            clientOptions);
    Func<IServiceProvider, IPushSubscriptionTransport> pushFactory =
        _ => transportFactory.CreatePush(
            new HttpPushSubscriptionTransportOptions("https://push.example/"),
            clientOptions);
    var recoveryPhraseProvider = new EmptyCallRecoveryPhraseProvider();
    Func<IServiceProvider, ICallSignalingTransport> callFactory =
        _ => transportFactory.CreateCallSignaling(
            new HttpCallSignalingTransportOptions("https://calls.example/"),
            recoveryPhraseProvider,
            clientOptions: clientOptions);
    Func<IServiceProvider, ICallIceConfigurationProvider> iceFactory =
        serviceProvider =>
            (ICallIceConfigurationProvider)serviceProvider.GetRequiredService<ICallSignalingTransport>();
    return constructor.Invoke(BindNamedArguments(
        constructor,
        new Dictionary<string, Func<Type, object?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["FeatureFlags"] = _ =>
                Deep.Client.Shared.Features.ClientFeatureFlags.ReleaseDefaults,
            ["RouterBaseUrls"] = _ => pinnedRouters,
            ["StorageBaseUrl"] = _ => null,
            ["RouterHttpClient"] = _ => routerHttpClient,
            ["RoutedTransportOptions"] = _ =>
                new RoutedSessionStorageTransportOptions(),
            ["RoutedEndpointPolicy"] = _ =>
                RoutedRuntimeEndpointPolicy.Production,
            ["ServiceTransportFactory"] = _ => transportFactory,
            ["ServiceTransportClientOptions"] = _ => clientOptions,
            ["MembershipRouteCatalogProvider"] = _ => null,
            ["RuntimeEnvironment"] = _ => runtime,
            ["CountryLookupFactory"] = CreateDefaultFactory,
            ["AvatarTransportFactory"] = _ => avatarFactory,
            ["AttachmentTransportFactory"] = _ => attachmentFactory,
            ["RuntimeBootstrapperFactory"] = CreateDefaultFactory,
            ["RuntimeFactory"] = CreateDefaultFactory,
            ["PushMetadataFactory"] = CreateDefaultFactory,
            ["PushTransportFactory"] = _ => pushFactory,
            ["CallTransportFactory"] = _ => callFactory,
            ["IceConfigurationFactory"] = _ => iceFactory,
            ["DesktopWorkspaceFactory"] = CreateDefaultFactory
        }));
}

static object?[] BindNamedArguments(
    ConstructorInfo constructor,
    IReadOnlyDictionary<string, Func<Type, object?>> values)
{
    var parameters = constructor.GetParameters();
    var bound = new object?[parameters.Length];
    for (var index = 0; index < parameters.Length; index++)
    {
        var parameter = parameters[index];
        if (parameter.Name is null ||
            !values.TryGetValue(parameter.Name, out var create))
        {
            throw new InvalidOperationException(
                $"No synthetic value is bound for {constructor.DeclaringType?.FullName}.{parameter.Name ?? "<unnamed>"}.");
        }

        bound[index] = create(parameter.ParameterType);
    }

    var parameterNames = parameters
        .Select(parameter => parameter.Name!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var extraNames = values.Keys
        .Where(name => !parameterNames.Contains(name))
        .ToArray();
    Require(
        extraNames.Length == 0,
        $"Synthetic bindings contain unknown constructor parameters: {string.Join(", ", extraNames)}.");
    return bound;
}

static Delegate CreateDefaultFactory(Type delegateType)
{
    var invoke = delegateType.GetMethod("Invoke")
        ?? throw new InvalidOperationException($"Not a delegate: {delegateType}.");
    var parameters = invoke.GetParameters()
        .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
        .ToArray();
    return Expression.Lambda(
        delegateType,
        Expression.Default(invoke.ReturnType),
        parameters).Compile();
}

static void ValidateFinalApplicationComposition(
    IServiceCollection descriptors,
    IServiceProvider provider,
    IReadOnlyList<PinnedRouterEndpoint> expectedRouters,
    string expectedFingerprint)
{
    Require(descriptors.Count == 65, "Final Windows Release app-owned descriptor count changed.");
    var descriptorFingerprint = DescriptorFingerprint(descriptors);
    Require(
        descriptorFingerprint == expectedFingerprint,
        $"Final Windows Release app-owned descriptor manifest changed: {descriptorFingerprint}.");
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
        "Final composition does not contain the expected synthetic pin set.");

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
    object inputs,
    IReadOnlyList<PinnedRouterEndpoint> expectedRouters)
{
    var mutated = new ServiceCollection();
    configureApplicationServices.Invoke(null, [mutated, inputs]);
    mutated.AddSingleton<ITransportRouteProvider>(
        new DirectStorageRouteProvider("https://storage.invalid/"));
    mutated.AddSingleton<ISessionMessageTransport>(new StubSessionBackend());
    mutated.TryAddSingleton(
        new HttpServiceTransportFactory(HttpServiceEndpointPolicy.Production)
            .CreateStorage(
                new SessionStorageMessageTransportOptions(
                    "https://storage.invalid/",
                    MetadataMode: SessionStorageMetadataMode.LegacyCompatibility)));
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

static void VerifyCreateMauiAppCallAllowlist(MethodInfo source, MethodInfo entrypoint)
{
    var sourceType = source.DeclaringType
        ?? throw new InvalidOperationException("CreateMauiApp has no declaring type.");
    var allowedLocalMethods = new HashSet<(Module Module, int Token)>
    {
        MethodIdentity(RequiredMethod(
            sourceType,
            "ValidateReleaseProcess",
            BindingFlags.NonPublic | BindingFlags.Static)),
        MethodIdentity(RequiredMethod(
            sourceType,
            "ConfigureWindowsHandlers",
            BindingFlags.NonPublic | BindingFlags.Static)),
        MethodIdentity(RequiredMethod(
            sourceType,
            "ResolveApplicationServiceInputs",
            BindingFlags.NonPublic | BindingFlags.Static)),
        MethodIdentity(entrypoint)
    };
    foreach (var instruction in ReadInstructions(source).Where(
                 static instruction => instruction.CalledMethod is not null))
    {
        var called = instruction.CalledMethod!;
        if (called.DeclaringType?.Assembly == source.DeclaringType?.Assembly)
        {
            Require(
                called is MethodInfo calledMethod &&
                allowedLocalMethods.Contains(MethodIdentity(calledMethod)),
                $"Unexpected same-app helper before final composition: {called.DeclaringType?.FullName}.{called.Name}.");
            if (called is not MethodInfo localMethod ||
                MethodIdentity(localMethod) != MethodIdentity(entrypoint))
            {
                Require(
                    called.GetParameters().All(static parameter =>
                        !IsSensitiveBuilderParameter(parameter.ParameterType)),
                    $"Pre-composition helper can receive builder/services state: {called.Name}.");
            }
            continue;
        }

        var declaring = called.DeclaringType?.FullName ?? string.Empty;
        var allowedFrameworkCall =
            (declaring == "Microsoft.Maui.Hosting.MauiApp" && called.Name == "CreateBuilder") ||
            (declaring == "Microsoft.Maui.Hosting.MauiAppBuilder" &&
             called.Name is "get_Services" or "Build") ||
            (declaring == "Microsoft.Maui.Controls.Hosting.AppHostBuilderExtensions" &&
             called.Name.StartsWith("UseMaui", StringComparison.Ordinal));
        Require(
            allowedFrameworkCall,
            $"Unexpected CreateMauiApp call target: {declaring}.{called.Name}.");
    }
}

static (Module Module, int Token) MethodIdentity(MethodInfo method) =>
    (method.Module, method.MetadataToken);

static bool IsSensitiveBuilderParameter(Type type) =>
    type == typeof(object) ||
    type.FullName is "Microsoft.Maui.Hosting.MauiAppBuilder" or
        "Microsoft.Extensions.DependencyInjection.IServiceCollection";

static void VerifyDeterministicCompositionEntrypoint(MethodInfo entrypoint)
{
    var root = ReadInstructions(entrypoint);
    var branches = root.Where(static instruction =>
        instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch).ToArray();
    Require(
        branches.Length == 0,
        "Final composition entrypoint contains branch control flow: " +
        string.Join(", ", branches.Select(static branch =>
            $"{branch.Offset}:{branch.OpCode.Name}")));
    Require(
        root.All(static instruction => instruction.OpCode.OperandType != OperandType.InlineSwitch),
        "Final composition entrypoint contains a switch.");
    foreach (var instruction in root)
    {
        var member = instruction.ReferencedMember;
        var declaring = member?.DeclaringType;
        var declaringName = declaring?.FullName ?? string.Empty;
        Require(
            declaring != typeof(Environment) &&
            declaring != typeof(AppContext) &&
            !declaringName.StartsWith("System.Reflection", StringComparison.Ordinal) &&
            !declaringName.StartsWith("System.Dynamic", StringComparison.Ordinal),
            $"Final composition reads ambient state or uses reflection/dynamic: {declaringName}.{member?.Name}.");
        Require(
            member?.Name is not (
                "ResolveRuntimeSetting" or "ResolveApplicationServiceInputs" or
                "GetEnvironmentVariable"),
            $"Final composition contains a config read: {member?.Name}.");
    }
    VerifyNoReachableForbiddenTransportTokens(entrypoint);
}

static void VerifyNoReachableForbiddenTransportTokens(MethodInfo entrypoint)
{
    var forbidden = new HashSet<string>(StringComparer.Ordinal)
    {
        typeof(DirectStorageRouteProvider).FullName!,
        typeof(SessionStorageMessageTransport).FullName!,
        typeof(StubSessionBackend).FullName!
    };
    var appAssembly = entrypoint.DeclaringType!.Assembly;
    var pending = new Stack<MethodInfo>();
    var visited = new HashSet<(Module Module, int Token)>();
    pending.Push(entrypoint);
    while (pending.Count > 0)
    {
        var method = pending.Pop();
        if (!visited.Add((method.Module, method.MetadataToken)))
        {
            continue;
        }
        if (method.GetMethodBody() is null)
        {
            continue;
        }
        foreach (var instruction in ReadInstructions(method))
        {
            var memberType = instruction.ReferencedMember switch
            {
                Type type => type,
                MemberInfo member => member.DeclaringType,
                _ => null
            };
            Require(
                memberType is null || !forbidden.Contains(memberType.FullName ?? string.Empty),
                $"Reachable direct/stub token exists: {memberType?.FullName}.");
            if (instruction.CalledMethod is MethodInfo called &&
                called.DeclaringType?.Assembly == appAssembly)
            {
                pending.Push(called);
            }
        }
    }
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
    AssertRejected(
        () => VerifyPreEntrypointBypassFixture(
            RequiredMethod(
                typeof(CompiledGuardFixtures),
                nameof(CompiledGuardFixtures.RegisterExtraBeforeEntrypoint),
                BindingFlags.Public | BindingFlags.Static)),
        "pre-entrypoint RegisterExtra bypass");
    AssertRejected(
        () => VerifyDeterministicCompositionEntrypoint(
            RequiredMethod(
                typeof(CompiledGuardFixtures),
                nameof(CompiledGuardFixtures.EnvironmentConditionalDirectStub),
                BindingFlags.Public | BindingFlags.Static)),
        "environment-conditional direct/stub bypass");
}

static void AssertNameBoundConstructorBindingIsOrderIndependent()
{
    var constructor = typeof(NameBoundBindingFixture).GetConstructors().Single();
    var values = new Dictionary<string, Func<Type, object?>>(
        StringComparer.OrdinalIgnoreCase)
    {
        ["First"] = _ => 41,
        ["Second"] = _ => "bound-by-name"
    };
    var fixture = (NameBoundBindingFixture)constructor.Invoke(
        BindNamedArguments(constructor, values));
    Require(fixture.First == 41, "Name-bound constructor integer was misbound.");
    Require(
        fixture.Second == "bound-by-name",
        "Name-bound constructor string was misbound.");
    AssertRejected(
        () => BindNamedArguments(
            constructor,
            new Dictionary<string, Func<Type, object?>>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["First"] = _ => 41
            }),
        "missing name-bound constructor value");
}

static void VerifyPreEntrypointBypassFixture(MethodInfo source)
{
    foreach (var called in ReadInstructions(source)
                 .Select(static instruction => instruction.CalledMethod)
                 .Where(static method => method is not null))
    {
        if (called!.DeclaringType == typeof(CompiledGuardFixtures) &&
            called.Name == "RegisterExtra")
        {
            Require(
                called.GetParameters().All(static parameter =>
                    !IsSensitiveBuilderParameter(parameter.ParameterType)),
                "Unexpected helper can receive services before final composition.");
        }
    }
}

static void AssertRejected(Action action, string fixture)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        return;
    }
    throw new InvalidOperationException($"Negative fixture was accepted: {fixture}.");
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
        MemberInfo? referencedMember = null;
        int[] branchTargets = [];
        switch (opcode.OperandType)
        {
            case OperandType.InlineMethod:
                EnsureAvailable(il, offset, sizeof(int));
                calledMethod = method.Module.ResolveMethod(BitConverter.ToInt32(il, offset));
                referencedMember = calledMethod;
                offset += sizeof(int);
                break;
            case OperandType.InlineField:
                EnsureAvailable(il, offset, sizeof(int));
                referencedMember = method.Module.ResolveField(BitConverter.ToInt32(il, offset));
                offset += sizeof(int);
                break;
            case OperandType.InlineType:
                EnsureAvailable(il, offset, sizeof(int));
                referencedMember = method.Module.ResolveType(BitConverter.ToInt32(il, offset));
                offset += sizeof(int);
                break;
            case OperandType.InlineTok:
                EnsureAvailable(il, offset, sizeof(int));
                referencedMember = method.Module.ResolveMember(BitConverter.ToInt32(il, offset));
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
        instructions.Add(new IlInstruction(
            start,
            offset,
            opcode,
            calledMethod,
            referencedMember,
            branchTargets));
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
    MemberInfo? ReferencedMember,
    IReadOnlyList<int> BranchTargets);

sealed record NameBoundBindingFixture(string Second, int First);

sealed class EmptyCallRecoveryPhraseProvider : ICallRecoveryPhraseProvider
{
    public Task<string?> GetRecoveryPhraseAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(string.Empty);
}

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
    public static object RegisterExtraBeforeEntrypoint(IServiceCollection services)
    {
        RegisterExtra(services);
        ApplicationEntrypointMarker();
        return BuildMarker();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EnvironmentConditionalDirectStub(IServiceCollection services)
    {
        if (Environment.GetEnvironmentVariable("DEEP_GUARD_BYPASS") == "1")
        {
            services.AddSingleton<ITransportRouteProvider>(
                new DirectStorageRouteProvider("https://storage.invalid/"));
            services.AddSingleton<ISessionMessageTransport>(new StubSessionBackend());
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IndirectRegistrationHelper()
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterExtra(object services)
    {
        _ = services;
    }
}
