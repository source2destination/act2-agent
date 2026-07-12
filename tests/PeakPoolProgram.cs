using System;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: peak-pool gen ... | judge ...");
    return 1;
}

string command = args[0].ToLowerInvariant();
string[] rest = args[1..];

return command switch
{
    "gen" => PoolGen.RunGen(rest),
    "judge" => PoolGen.RunJudge(rest),
    _ => Fail(command)
};

static int Fail(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    return 1;
}
