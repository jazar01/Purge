namespace PurgeWindow;

/// <summary>
/// PurgeWindow.exe: the windows behind the Explorer right-click commands.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        LaunchArgs launch;
        try
        {
            launch = LaunchArgs.Parse(args);
        }
        catch (ArgumentException e)
        {
            ApplicationConfiguration.Initialize();
            MessageBox.Show($"{e.Message}\n\n{LaunchArgs.Usage}", "Purge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        switch (launch.Mode)
        {
            case LaunchMode.AddToExplorer:
            case LaunchMode.RemoveFromExplorer:
                try
                {
                    if (launch.Mode == LaunchMode.AddToExplorer)
                        ExplorerMenu.Add(Environment.ProcessPath!);
                    else
                        ExplorerMenu.Remove();
                    return 0;
                }
                catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    return 1;
                }
        }

        ApplicationConfiguration.Initialize();

        switch (launch.Mode)
        {
            case LaunchMode.Wipe:
                // Explorer starts one copy per selected file; the first copy shows the
                // window and the others hand their files to it.
                string channel = SelectionCollector.DefaultChannel;
                var collector = SelectionCollector.TryBecomePrimary(channel);
                if (collector is null && SelectionCollector.TrySend(channel, launch.Paths))
                    return 0;
                using (collector)
                    Application.Run(new WipeForm(launch.Paths, collector));
                break;

            case LaunchMode.Folder:
                Application.Run(new PurgeForm(launch.Paths.FirstOrDefault()));
                break;

            default:
                Application.Run(new SetupForm());
                break;
        }
        return 0;
    }
}
