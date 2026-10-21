using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mdf4Net.BusLogging;

namespace Mdf4Net.InteropProbe;

/// <summary>
/// What a reader must see in a file: per channel the time stamps and physical values, and for
/// bus logging files the CAN frames. The JSON format is shared with eng/interop/check_asammdf.py.
/// </summary>
/// <remarks>
/// Values are JSON numbers, strings, or <c>{"hex": "..."}</c> for byte arrays. Channel groups
/// are identified by their position among all channel groups that hold channels, which is how
/// asammdf numbers its groups.
/// </remarks>
internal sealed class Expectation
{
    public List<ExpectedChannel> Channels { get; } = new();

    public List<CanDataFrame> Frames { get; } = new();

    /// <summary>The measurement start time the file declares, if the scenario checks it.</summary>
    public DateTimeOffset? Start { get; set; }

    public static Expectation Load(string path)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
        var expectation = new Expectation();
        if (root["start"] is JsonNode start)
        {
            expectation.Start = DateTimeOffset.Parse(start.GetValue<string>(), CultureInfo.InvariantCulture);
        }

        foreach (JsonNode? node in root["channels"]?.AsArray() ?? new JsonArray())
        {
            expectation.Channels.Add(new ExpectedChannel(
                node!["group"]!.GetValue<int>(),
                node["name"]!.GetValue<string>(),
                node["unit"]?.GetValue<string>(),
                node["timestamps"]!.AsArray().Select(t => t!.GetValue<double>()).ToList(),
                node["values"]!.AsArray().Select(FromJson).ToList()));
        }

        foreach (JsonNode? node in root["can"]?.AsArray() ?? new JsonArray())
        {
            CanFrameFlags flags = (node!["ext"]!.GetValue<bool>() ? CanFrameFlags.Extended : 0)
                | (node["fd"]!.GetValue<bool>() ? CanFrameFlags.FlexibleDataRate : 0)
                | (node["brs"]?.GetValue<bool>() == true ? CanFrameFlags.BitRateSwitch : 0);
            expectation.Frames.Add(new CanDataFrame(
                node["t"]!.GetValue<double>(),
                node["id"]!.GetValue<uint>(),
                Convert.FromHexString(node["data"]!.GetValue<string>()),
                flags,
                node["bus"]!.GetValue<byte>(),
                node["tx"]?.GetValue<bool>() == true ? CanDirection.Transmit : CanDirection.Receive));
        }

        return expectation;
    }

    public void Save(string path)
    {
        var channels = new JsonArray();
        foreach (ExpectedChannel channel in Channels)
        {
            channels.Add(new JsonObject
            {
                ["group"] = channel.Group,
                ["name"] = channel.Name,
                ["unit"] = channel.Unit,
                ["timestamps"] = new JsonArray(channel.Timestamps.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                ["values"] = new JsonArray(channel.Values.Select(ToJson).ToArray()),
            });
        }

        var frames = new JsonArray();
        foreach (CanDataFrame frame in Frames)
        {
            frames.Add(new JsonObject
            {
                ["t"] = frame.Timestamp,
                ["id"] = frame.Id,
                ["ext"] = frame.IsExtended,
                ["fd"] = frame.IsFd,
                ["brs"] = (frame.Flags & CanFrameFlags.BitRateSwitch) != 0,
                ["bus"] = frame.BusChannel,
                ["tx"] = frame.Direction == CanDirection.Transmit,
                ["dlc"] = frame.Dlc,
                ["data"] = Convert.ToHexString(frame.Data.ToArray()),
            });
        }

        var root = new JsonObject { ["channels"] = channels, ["can"] = frames };
        if (Start is DateTimeOffset start)
        {
            root["start"] = start.ToString("O", CultureInfo.InvariantCulture);
        }

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    public List<string> Verify(string path)
    {
        var problems = new List<string>();
        try
        {
            using Mdf4File file = Mdf4File.Open(path);
            // asammdf computes the stored nanoseconds through a double (2,048 ns resolution near the
            // year 2500), so its files hold a start time to within a few microseconds.
            if (Start is DateTimeOffset start && (Math.Abs((file.StartTime - start).Ticks) > 40 || file.StartTime.Offset != start.Offset))
            {
                problems.Add($"start time {file.StartTime:O}, expected {start:O}");
            }

            List<Mdf4ChannelGroup> groups = file.ChannelGroups.Where(g => !g.IsVariableLengthSignalData).ToList();
            foreach (ExpectedChannel expected in Channels)
            {
                if (expected.Group >= groups.Count)
                {
                    problems.Add($"group {expected.Group} missing");
                    continue;
                }

                Mdf4ChannelGroup group = groups[expected.Group];
                Mdf4Channel? channel = group.FindChannel(expected.Name);
                if (channel is null)
                {
                    problems.Add($"group {expected.Group}: channel '{expected.Name}' missing");
                    continue;
                }

                string label = $"{expected.Group}/{expected.Name}";
                if (expected.Unit is not null && channel.Unit != expected.Unit)
                {
                    problems.Add($"{label}: unit '{channel.Unit}', expected '{expected.Unit}'");
                }

                double[] timestamps = group.ReadTimestamps();
                object?[] values = channel.ReadValues();
                if (values.Length != expected.Values.Count || timestamps.Length != expected.Timestamps.Count)
                {
                    problems.Add($"{label}: {values.Length} values / {timestamps.Length} time stamps, expected {expected.Values.Count} / {expected.Timestamps.Count}");
                    continue;
                }

                for (int i = 0; i < values.Length; i++)
                {
                    if (!Close(timestamps[i], expected.Timestamps[i]))
                    {
                        problems.Add($"{label}[{i}]: time {timestamps[i]}, expected {expected.Timestamps[i]}");
                        break;
                    }

                    if (!Same(values[i], expected.Values[i]))
                    {
                        problems.Add($"{label}[{i}]: {Show(values[i])}, expected {Show(expected.Values[i])}");
                        break;
                    }
                }
            }

            if (Frames.Count > 0)
            {
                IReadOnlyList<CanDataFrame> frames = file.ReadCanDataFrames();
                if (frames.Count != Frames.Count)
                {
                    problems.Add($"{frames.Count} CAN frames, expected {Frames.Count}");
                }
                else
                {
                    for (int i = 0; i < frames.Count; i++)
                    {
                        CanDataFrame a = frames[i], e = Frames[i];
                        if (!Close(a.Timestamp, e.Timestamp) || a.Id != e.Id || a.Flags != e.Flags || a.BusChannel != e.BusChannel
                            || a.Direction != e.Direction || !a.Data.Span.SequenceEqual(e.Data.Span))
                        {
                            problems.Add($"CAN frame {i}: {a}, expected {e}");
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is Mdf4FormatException or InvalidOperationException or NotSupportedException)
        {
            problems.Add($"{ex.GetType().Name}: {ex.Message}");
        }

        return problems;
    }

    private static bool Close(double a, double b)
        => a.Equals(b) || Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

    private static bool Same(object? actual, object? expected) => (actual, expected) switch
    {
        (null, null) => true,
        (byte[] a, byte[] e) => a.AsSpan().SequenceEqual(e),
        (string a, string e) => a == e,
        (double a, double e) => Close(a, e) || (double.IsNaN(a) && double.IsNaN(e)),
        _ => false,
    };

    private static string Show(object? value) => value switch
    {
        null => "null",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static object? FromJson(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o when o["hex"] is not null => Convert.FromHexString(o["hex"]!.GetValue<string>()),
        JsonObject o when o["float"] is not null => double.Parse(o["float"]!.GetValue<string>(), CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue(out string? s) => s,
        JsonValue v => v.GetValue<double>(),
        _ => throw new FormatException($"Unexpected JSON value {node.ToJsonString()}"),
    };

    private static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        byte[] bytes => new JsonObject { ["hex"] = Convert.ToHexString(bytes) },
        string s => JsonValue.Create(s),
        // JSON has no NaN or infinity; they travel as {"float": "NaN"}.
        double d when double.IsNaN(d) || double.IsInfinity(d) => new JsonObject { ["float"] = d.ToString(CultureInfo.InvariantCulture) },
        double d => JsonValue.Create(d),
        _ => throw new FormatException($"Unexpected value type {value.GetType()}"),
    };
}

internal sealed record ExpectedChannel(int Group, string Name, string? Unit, List<double> Timestamps, List<object?> Values);
