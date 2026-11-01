using BDM.Cli;

var dataDir = Environment.GetEnvironmentVariable("BDM_DATA_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterDownloadManager");
return await CliRunner.RunAsync(args, Console.Out, Console.Error, dataDir);
