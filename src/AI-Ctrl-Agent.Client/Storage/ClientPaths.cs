namespace AiCtrl.Client.Storage;

public static class ClientPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AiCtrl");

    public static string DatabasePath => Path.Combine(DataDirectory, "client.db");

    public static string LocalTokenPath => Path.Combine(DataDirectory, "local-token");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);
}
