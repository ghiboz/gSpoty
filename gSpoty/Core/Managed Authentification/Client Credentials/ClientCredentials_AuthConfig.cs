
[System.Serializable]
public class ClientCredentials_AuthConfig : AuthorizationConfig
{
    /// <summary>
    /// Secret Id of your app, can be located in your Spotify dashboard. Don't have one? Don't have an id? Go here: https://developer.spotify.com/dashboard/
    /// </summary>
    public string ClientSecret = "";

    /// <summary>
    /// Your client id, found in your Spotify Dashboard. Don't have an id? Go here: https://developer.spotify.com/dashboard/
    /// </summary>
    public string ClientID = "";

    /// <summary>
    /// The redirect uri used to pass Spotify authentification onto your app. This uri needs to be in your Spotify Dashboard. Dont change this if you don't know what you are doing.
    /// </summary>
    public string RedirectUri = "http://localhost:5000/callback";

    /// <summary>
    /// Port number to use for recieving Spotify auth from the browser. Should be the same value in your Redirect uri
    /// Don't change this if you don't know what you are doing
    /// </summary>
    public int ServerPort = 5000;

}
