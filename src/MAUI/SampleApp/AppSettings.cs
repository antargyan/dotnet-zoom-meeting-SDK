namespace SampleApp;

public class AppSettings
{
    public const string ZOOM_MEETING_NUMBER = "enter_meetingno";
    public const string ZOOM_MEETING_PASSWORD = "enter_pw";
    public const string ZOOM_JWT = "enter_jwt";

    /// <summary>
    /// Auth worker that mints the short-lived SDK token (POST /sdk-token) and ZAK (POST /zak).
    /// The SDK *secret* lives there, never in this app. Taken from the RioConf project's
    /// ZoomDefaults.AuthEndpoint - point it at your own worker if you have one.
    /// </summary>
    public const string ZOOM_AUTH_ENDPOINT = "https://zoom-meetingsdk-auth-endpoint.sam-967.workers.dev";

    /// <summary>Zoom user to act as when fetching a ZAK. "me" resolves to the S2S app owner.</summary>
    public const string ZOOM_USER_ID = "me";
}
