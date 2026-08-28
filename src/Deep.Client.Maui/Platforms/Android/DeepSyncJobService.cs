#if ANDROID
using Android.App;
using Android.App.Job;
using Android.Content;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Services;
using PersistableBundle = Android.OS.PersistableBundle;

namespace Deep.Client.Maui;

[Service(
    Name = "network.xpoint.deep.DeepSyncJobService",
    Permission = "android.permission.BIND_JOB_SERVICE",
    Exported = false)]
public sealed class DeepSyncJobService : JobService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource> executions = new();

    public override bool OnStartJob(JobParameters? parameters)
    {
        if (parameters is null)
        {
            return false;
        }

        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        if (executions.TryRemove(parameters.JobId, out var previous))
        {
            previous.Cancel();
        }

        executions[parameters.JobId] = cancellation;
        _ = ExecuteAsync(parameters, cancellation);
        return true;
    }

    public override bool OnStopJob(JobParameters? parameters)
    {
        if (parameters is not null && executions.TryRemove(parameters.JobId, out var cancellation))
        {
            cancellation.Cancel();
        }

        return true;
    }

    private async Task ExecuteAsync(JobParameters parameters, CancellationTokenSource executionCancellation)
    {
        var retry = true;
        try
        {
            var cancellationToken = executionCancellation.Token;
            if (AndroidBackgroundSyncScheduler.IsUnsubscribeRetry(parameters))
            {
                retry = !await TryRetryPendingPushUnsubscribeAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (AndroidBackgroundSyncScheduler.TryReadPushEnvelope(parameters, out var pushData))
            {
                var authentication = await PushNotificationBackgroundHandler
                    .AuthenticateAsync(
                        pushData,
                        BackgroundSyncBridge.PublishScheduledSync,
                        cancellationToken)
                    .ConfigureAwait(false);
                Android.Util.Log.Info("DeepPush", $"Push authentication status: {authentication.Status}.");
                if (authentication.Status == PushAuthenticationStatus.Rejected)
                {
                    retry = false;
                    return;
                }

                if (authentication.Status == PushAuthenticationStatus.Replay &&
                    !BackgroundSyncBridge.HasPendingSync())
                {
                    retry = false;
                    return;
                }
            }

            MauiBackgroundSyncRunner.Result synchronization;
            try
            {
                synchronization = await MauiBackgroundSyncRunner.SynchronizeDeferredCompletionAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                await PresentPendingIncomingMessageNotificationAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }

            retry = !synchronization.Succeeded;
            await PresentPendingIncomingMessageNotificationAsync(cancellationToken).ConfigureAwait(false);

            if (synchronization.Succeeded)
            {
                BackgroundSyncBridge.MarkHandled();
            }

            Android.Util.Log.Info("DeepPush", synchronization.Succeeded
                ? "Background synchronization completed."
                : "Background synchronization requested a retry.");
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
            retry = true;
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("Android.BackgroundSync", exception);
            var failureCode = SyncFailureCodeClassifier.Classify(exception);
            Android.Util.Log.Warn(
                "DeepPush",
                $"Background synchronization failed; code={failureCode}; retrying.");
            retry = true;
        }
        finally
        {
            var ownsExecution = ((ICollection<KeyValuePair<int, CancellationTokenSource>>)executions)
                .Remove(new KeyValuePair<int, CancellationTokenSource>(parameters.JobId, executionCancellation));
            executionCancellation.Dispose();
            if (ownsExecution)
            {
                JobFinished(parameters, retry);
            }
        }
    }

    private static async Task<bool> TryRetryPendingPushUnsubscribeAsync(CancellationToken cancellationToken)
    {
        var services = IPlatformApplication.Current?.Services ?? App.Services;
        return await PushUnsubscribeRetryBootstrapper
            .TryRetryAsync(services, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PresentPendingIncomingMessageNotificationAsync(CancellationToken cancellationToken)
    {
        var services = IPlatformApplication.Current?.Services ?? App.Services;
        var activeConversationTracker = services?.GetService<IActiveConversationTracker>()
            ?? throw new InvalidOperationException("The active conversation tracker is unavailable.");
        var coordinator = new IncomingMessageNotificationCoordinator(
            MauiBackgroundSyncRunner.ListPendingIncomingMessageNotificationIdsAsync,
            MauiBackgroundSyncRunner.MarkIncomingMessageNotificationsPresentedAsync,
            activeConversationTracker,
            (notificationId, _) =>
            {
                AndroidPushNotificationPresenter.Show(this, notificationId);
                return Task.CompletedTask;
            },
            BackgroundSyncBridge.PublishScheduledSync);
        var presentedCount = await coordinator.PresentPendingAsync(cancellationToken).ConfigureAwait(false);
        if (presentedCount > 0)
        {
            Android.Util.Log.Info("DeepPush", $"Presented notification for {presentedCount} incoming message(s).");
        }
    }
}

public static class AndroidBackgroundSyncScheduler
{
    private const int DelayedSyncJobId = 0x445350;
    private const int PushSyncJobId = 0x445351;
    private const int RegularRetryJobId = 0x445352;
    private const int UnsubscribeRetryJobId = 0x445353;
    private const string PushEnvelopeKey = "deep.push.envelope";
    private const string PushVersionKey = "deep.push.version";
    private static readonly TimeSpan RegularRetryInterval = TimeSpan.FromMinutes(15);

    public static void Schedule(TimeSpan minimumDelay)
        => ScheduleCore(DelayedSyncJobId, minimumDelay, extras: null);

    public static void ScheduleRegularRetry()
    {
        var (context, scheduler) = GetScheduler();
        using var existing = scheduler.GetPendingJob(RegularRetryJobId);
        if (existing is not null)
        {
            return;
        }

        using var serviceClass = Java.Lang.Class.FromType(typeof(DeepSyncJobService))
            ?? throw new InvalidOperationException("Android background synchronization service is unavailable.");
        using var component = new ComponentName(context, serviceClass);
        using var builder = new JobInfo.Builder(RegularRetryJobId, component);
        builder.SetRequiredNetworkType(NetworkType.Any);
        builder.SetPeriodic((long)RegularRetryInterval.TotalMilliseconds);
        builder.SetBackoffCriteria(
            (long)TimeSpan.FromSeconds(30).TotalMilliseconds,
            BackoffPolicy.Exponential);
        ScheduleBuiltJob(scheduler, builder);
    }

    public static void CancelRegularRetry()
    {
        var context = Android.App.Application.Context;
        var scheduler = (JobScheduler?)context?.GetSystemService(Context.JobSchedulerService);
        scheduler?.Cancel(RegularRetryJobId);
    }

    public static void ScheduleUnsubscribeRetry() =>
        ScheduleCore(UnsubscribeRetryJobId, TimeSpan.Zero, extras: null, skipIfPending: true);

    public static void SchedulePush(string encodedEnvelope, string protocolVersion)
    {
        if (string.IsNullOrWhiteSpace(encodedEnvelope) || encodedEnvelope.Length > 8192 ||
            string.IsNullOrWhiteSpace(protocolVersion) || protocolVersion.Length > 8)
        {
            throw new ArgumentException("Push job envelope is invalid.");
        }

        using var extras = new PersistableBundle();
        extras.PutString(PushEnvelopeKey, encodedEnvelope);
        extras.PutString(PushVersionKey, protocolVersion);
        ScheduleCore(PushSyncJobId, TimeSpan.Zero, extras);
    }

    internal static bool TryReadPushEnvelope(
        JobParameters parameters,
        out IReadOnlyDictionary<string, string> data)
    {
        var envelope = parameters.Extras?.GetString(PushEnvelopeKey);
        var version = parameters.Extras?.GetString(PushVersionKey);
        if (string.IsNullOrWhiteSpace(envelope) || string.IsNullOrWhiteSpace(version))
        {
            data = new Dictionary<string, string>();
            return false;
        }

        data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enc_payload"] = envelope,
            ["spns"] = version
        };
        return true;
    }

    internal static bool IsUnsubscribeRetry(JobParameters parameters) =>
        parameters.JobId == UnsubscribeRetryJobId;

    private static void ScheduleCore(
        int jobId,
        TimeSpan minimumDelay,
        PersistableBundle? extras,
        bool skipIfPending = false)
    {
        if (minimumDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumDelay));
        }

        var (context, scheduler) = GetScheduler();
        if (skipIfPending)
        {
            using var existing = scheduler.GetPendingJob(jobId);
            if (existing is not null)
            {
                return;
            }
        }

        var delayMilliseconds = (long)Math.Min(minimumDelay.TotalMilliseconds, int.MaxValue);
        using var serviceClass = Java.Lang.Class.FromType(typeof(DeepSyncJobService))
            ?? throw new InvalidOperationException("Android background synchronization service is unavailable.");
        using var component = new ComponentName(context, serviceClass);
        using var builder = new JobInfo.Builder(jobId, component);
        builder.SetRequiredNetworkType(NetworkType.Any);
        builder.SetMinimumLatency(delayMilliseconds);
        builder.SetOverrideDeadline(delayMilliseconds + (long)TimeSpan.FromMinutes(5).TotalMilliseconds);
        builder.SetBackoffCriteria(
            (long)TimeSpan.FromSeconds(30).TotalMilliseconds,
            BackoffPolicy.Exponential);
        if (extras is not null)
        {
            builder.SetExtras(extras);
        }

        ScheduleBuiltJob(scheduler, builder);
    }

    private static (Context Context, JobScheduler Scheduler) GetScheduler()
    {
        var context = Android.App.Application.Context
            ?? throw new InvalidOperationException("Android application context is unavailable.");
        var scheduler = (JobScheduler?)context.GetSystemService(Context.JobSchedulerService)
            ?? throw new InvalidOperationException("Android JobScheduler is unavailable.");
        return (context, scheduler);
    }

    private static void ScheduleBuiltJob(JobScheduler scheduler, JobInfo.Builder builder)
    {
        using var job = builder.Build()
            ?? throw new InvalidOperationException("Android background synchronization job could not be created.");
        var result = scheduler.Schedule(job);
        if (result != JobScheduler.ResultSuccess)
        {
            throw new InvalidOperationException("Android background synchronization could not be scheduled.");
        }
    }
}
#endif
