namespace ChannelWave.UI;

public enum RootSection { Home, Library, Settings, Profile }
public sealed record AppRoute(string Page)
{
    public bool IsRoot => Page is "home" or "library" or "settings" or "account";
    public RootSection Root => Page switch
    {
        "home" or "customize" => RootSection.Home,
        "settings" or "storage" or "downloads" => RootSection.Settings,
        "account" or "about" => RootSection.Profile,
        _ => RootSection.Library
    };
    public string Parent => Page switch
    {
        "customize" => "home",
        "storage" or "downloads" => "settings",
        "about" => "account",
        _ => "library"
    };
}
