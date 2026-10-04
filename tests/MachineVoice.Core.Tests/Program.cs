namespace MachineVoice.Core.Tests;

/// <summary>Not used by the test runner; child processes of <see cref="AvSpeechEngineTests"/> start here.</summary>
static class Program
{
    public static int Main(string[] args)
    {
        if (OperatingSystem.IsMacOS() && args is [AvSpeechEngineTests.ChildVerb, var scenario])
            return AvSpeechEngineTests.RunChild(scenario);

        Console.Error.WriteLine("Run the tests with dotnet test.");
        return 2;
    }
}
