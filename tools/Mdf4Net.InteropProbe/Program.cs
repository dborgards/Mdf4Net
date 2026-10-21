using System;
using System.IO;
using Mdf4Net.InteropProbe;

// Interoperability probe, driven by eng/interop/check_asammdf.py:
//
//   write  <dir>   writes one .mf4 per scenario plus <name>.json with the values that any
//                  conforming reader must see (asammdf checks them);
//   verify <dir>   reads every <name>.mf4 in <dir> that has a <name>.json next to it (written by
//                  asammdf) and compares what Mdf4Net reads with the JSON.
//
// Exit code 0 on success, 1 on a mismatch, 2 on a usage error.
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Mdf4Net.InteropProbe write|verify <directory>");
    return 2;
}

string directory = Path.GetFullPath(args[1]);
switch (args[0])
{
    case "write":
        Directory.CreateDirectory(directory);
        foreach (Scenario scenario in Scenarios.All)
        {
            string path = Path.Combine(directory, scenario.Name + ".mf4");
            Expectation expectation = scenario.Write(path);
            expectation.Save(Path.Combine(directory, scenario.Name + ".json"));
            Console.WriteLine($"wrote {scenario.Name}.mf4");
        }

        return 0;

    case "verify":
        int failures = 0, files = 0;
        foreach (string json in Directory.GetFiles(directory, "*.json"))
        {
            string mf4 = Path.ChangeExtension(json, ".mf4");
            if (!File.Exists(mf4))
            {
                continue;
            }

            files++;
            var problems = Expectation.Load(json).Verify(mf4);
            Console.WriteLine($"{(problems.Count == 0 ? "ok  " : "FAIL")}  {Path.GetFileName(mf4)}");
            foreach (string problem in problems)
            {
                Console.WriteLine($"        {problem}");
            }

            failures += problems.Count == 0 ? 0 : 1;
        }

        Console.WriteLine($"\n{files} file(s) verified, {failures} failed");
        return failures == 0 && files > 0 ? 0 : 1;

    default:
        Console.Error.WriteLine($"unknown command '{args[0]}'");
        return 2;
}
