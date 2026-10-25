if (args.Length == 2 && args[0] == "--recorded") PressureChecks.CheckRecorded(args[1]);
else
{
    PressureChecks.Run();
    await BatchChecks.RunAsync();
}
