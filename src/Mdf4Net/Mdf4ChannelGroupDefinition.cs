using System;
using System.Collections.Generic;

namespace Mdf4Net;

/// <summary>
/// A channel group to write: its channels and metadata. Add it to a writer with
/// <see cref="Mdf4Writer.AddChannelGroup(Mdf4ChannelGroupDefinition)"/>; after that it can no
/// longer be changed.
/// </summary>
public sealed class Mdf4ChannelGroupDefinition
{
    private readonly List<Mdf4ChannelDefinition> _channels = new();

    /// <summary>Creates an empty channel group.</summary>
    /// <param name="name">The acquisition name of the group, shown by most tools.</param>
    public Mdf4ChannelGroupDefinition(string? name)
    {
        Name = name;
    }

    /// <summary>The acquisition name.</summary>
    public string? Name { get; }

    /// <summary>The group comment.</summary>
    public string? Comment { get; init; }

    /// <summary>The acquisition source.</summary>
    public Mdf4SourceDefinition? Source { get; init; }

    /// <summary>The separator used in structure member names, e.g. <c>.</c>.</summary>
    public char? PathSeparator { get; init; }

    /// <summary>
    /// Group flags. Only <see cref="Mdf4ChannelGroupFlags.BusEvent"/> and
    /// <see cref="Mdf4ChannelGroupFlags.PlainBusEvent"/> may be set.
    /// </summary>
    public Mdf4ChannelGroupFlags Flags { get; init; }

    /// <summary>The channels, in the order they were added.</summary>
    public IReadOnlyList<Mdf4ChannelDefinition> Channels => _channels;

    /// <summary>True once the group was added to a writer.</summary>
    public bool IsFrozen { get; private set; }

    /// <summary>Adds a channel and returns it, to be used as the handle for setting its value.</summary>
    public Mdf4ChannelDefinition Add(Mdf4ChannelDefinition channel)
    {
        if (channel is null)
        {
            throw new ArgumentNullException(nameof(channel));
        }

        if (IsFrozen)
        {
            throw new InvalidOperationException("The channel group was already added to a writer and can no longer be changed.");
        }

        if (_channels.Contains(channel))
        {
            throw new ArgumentException($"Channel '{channel.Name}' was already added.", nameof(channel));
        }

        channel.Validate();
        _channels.Add(channel);
        return channel;
    }

    /// <summary>Adds a time master channel (64-bit float seconds) and returns it.</summary>
    public Mdf4ChannelDefinition AddTime(string name = "t") => Add(Mdf4ChannelDefinition.Time(name));

    internal void Freeze() => IsFrozen = true;
}
