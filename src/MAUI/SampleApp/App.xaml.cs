namespace SampleApp;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();

        MainPage = new AppShell();
    }

    protected override void OnStart()
    {
        // Fetch the SDK token first, then initialize - the same order the RioConf Windows head uses,
        // since there is no point initializing the SDK if the auth endpoint is unreachable.
        //
        // AppSettings.ZOOM_JWT is honoured as an override when set to a real token, so a hardcoded
        // JWT still works; otherwise the token comes from the auth endpoint.
        _ = InitializeZoomAsync();
    }

    private static async Task InitializeZoomAsync()
    {
        var service = MauiProgram.ZoomSDKService;

        try
        {
            var token = AppSettings.ZOOM_JWT;

            if (string.IsNullOrWhiteSpace(token) || token.StartsWith("enter_", StringComparison.Ordinal))
                token = await ZoomAuthClient.GetSdkTokenAsync();

            // The SDK insists on the main thread.
            await MainThread.InvokeOnMainThreadAsync(() => service.InitZoomLib(token));
        }
        catch (Exception e)
        {
            service.LastError = e.Message;
            service.ZoomInitStatus = ZoomInitStatus.Failed;
        }
    }
}
