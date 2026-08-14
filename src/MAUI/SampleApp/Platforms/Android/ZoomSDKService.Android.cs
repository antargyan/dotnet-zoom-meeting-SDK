using System.ComponentModel;
using System.Runtime.CompilerServices;
using SampleApp.Platforms.Android;
using US.Zoom.Sdk;
using Application = Android.App.Application;

[assembly: Microsoft.Maui.Controls.Dependency(typeof(DroidZoomSDKService))]
namespace SampleApp.Platforms.Android;

/// <summary>
/// Yes, we're mixing a service and a viewmodel. But what do you want from me, it's a sample app!
/// </summary>
public class DroidZoomSDKService 
    : Java.Lang.Object, IZoomSDKService, IZoomSDKInitializeListener
{
    private ZoomInitStatus zoomInitStatus;

    public DroidZoomSDKService()
    {
        ZoomInitStatus = ZoomInitStatus.NotStarted;
    }
        

    // Create the OnPropertyChanged method to raise the event
    // The calling member's name will be used as the parameter.
    protected void OnPropertyChanged([CallerMemberName] string name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // Declare the event
    public event PropertyChangedEventHandler PropertyChanged;

    public ZoomInitStatus ZoomInitStatus
    {
        get => zoomInitStatus;
        set
        {
            if (value == zoomInitStatus) return;
            zoomInitStatus = value;
            OnPropertyChanged();
        }
    }

    public string ZoomVersion { get; set; }

    private string lastError = "";

    public string LastError
    {
        get => lastError;
        set { if (value != lastError) { lastError = value; OnPropertyChanged(); } }
    }

    public void InitZoomLib(string token)
    {
        try
        {
            ZoomInitStatus = ZoomInitStatus.InProgress;

            var zoomInitParams = new ZoomSDKInitParams
            {
                JwtToken = token,
                // Mirrors the RioConf Windows head's InitParam { web_domain, enable_log }.
                Domain = "zoom.us",
                EnableLog = true
            };

            ZoomSDK.Instance.Initialize(Application.Context, this, zoomInitParams);
        }
        catch (Exception e)
        {
            // Was swallowed silently, leaving only "Failed" on screen with no way to tell an invalid
            // token from a binding problem.
            global::Android.Util.Log.Error("ZoomSampleApp", $"InitZoomLib threw: {e}");
            LastError = e.Message;
            ZoomInitStatus = ZoomInitStatus.Failed;
        }
    }

    public async Task JoinMeeting(string meetingID, string meetingPassword, string displayName = "Zoom Demo")
    {
        // Android's JoinMeetingParams has no ZAK field - an anonymous join never takes one. (The
        // Windows SDK does accept a userZAK on join; hosting on Android needs
        // StartMeetingParamsWithoutLogin.ZoomAccessToken instead.)
        var meetingService = ZoomSDK.Instance.MeetingService;

        var result = meetingService.JoinMeetingWithParams(global::Android.App.Application.Context,
            new JoinMeetingParams
            {
                MeetingNo = meetingID,
                DisplayName = displayName,
                Password = meetingPassword
            }, new JoinMeetingOptions { });

        global::Android.Util.Log.Info("ZoomSampleApp", $"JoinMeetingWithParams returned {result}");

        if (result != MeetingError.MeetingErrorSuccess)
            LastError = $"join error {result}";

        await Task.CompletedTask;
    }

    private static string DescribeInitError(int errorCode) => errorCode switch
    {
        ZoomError.ZoomErrorSuccess => "success",
        ZoomError.ZoomErrorInvalidArguments => "invalid arguments - the JWT is malformed or empty",
        ZoomError.ZoomErrorIllegalAppKeyOrSecret => "illegal app key or secret",
        ZoomError.ZoomErrorNetworkUnavailable => "network unavailable",
        ZoomError.ZoomErrorAuthretTokenwrong => "JWT rejected",
        ZoomError.ZoomErrorAuthretKeyOrSecretError => "key or secret error",
        ZoomError.ZoomErrorAuthretAccountNotSupport => "account not supported",
        ZoomError.ZoomErrorAuthretAccountNotEnableSdk => "account has the Meeting SDK disabled",
        ZoomError.ZoomErrorDeviceNotSupported => "device not supported",
        ZoomError.ZoomErrorDomainDontSupport => "domain not supported",
        _ => "see us.zoom.sdk.ZoomError"
    };

    public void OnZoomAuthIdentityExpired() { }

    public void OnZoomSDKInitializeResult(int errorCode, int internalErrorCode)
    {
        // Logged so a failure is diagnosable from logcat (adb logcat -s ZoomSampleApp) instead of
        // showing only "Failed" in the UI. errorCode 1 = invalid/expired JWT.
        // global:: is required - this file's own namespace is SampleApp.Platforms.Android, which
        // shadows the framework's Android namespace (CS0234).
        global::Android.Util.Log.Info("ZoomSampleApp",
            $"OnZoomSDKInitializeResult errorCode={errorCode} internalErrorCode={internalErrorCode}");

        if (errorCode == ZoomError.ZoomErrorSuccess)
        {
            LastError = "";
            // Android exposes this as getVersion(Context), not as a property.
            ZoomVersion = ZoomSDK.Instance.GetVersion(Application.Context) ?? "";
            ZoomInitStatus = ZoomInitStatus.Success;
            //Add listeners according to your needs
            //ZoomSDK.Instance.InMeetingService.AddListener(new YourInMeetingServiceListener());
            //ZoomSDK.Instance.MeetingService.AddListener(new YourMeetingServiceListener());
        }
        else
        {
            LastError = $"init error {errorCode} ({DescribeInitError(errorCode)})";
            ZoomInitStatus = ZoomInitStatus.Failed;
        }
    }
}