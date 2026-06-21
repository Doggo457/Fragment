using System;
using System.Collections.Generic;

namespace Fragment.Models;

/// <summary>
/// One piece of the edit sequence: a half-open [In, Out) range taken from a single source file
/// (matching ffmpeg's trim, which is [start, end) — so a split at time T puts frame T in the right
/// half only, with no duplication). The editor's timeline is just an ordered list of these; cutting
/// splits/removes segments, merging appends them, and export trims each and concatenates the lot.
/// </summary>
public sealed class EditorSegment
{
    public EditorSegment(string sourceFile, double inSec, double outSec)
    {
        SourceFile = sourceFile;
        InSec = inSec;
        OutSec = outSec;
    }

    public string SourceFile { get; set; }
    public double InSec { get; set; }
    public double OutSec { get; set; }

    public double Duration => Math.Max(0, OutSec - InSec);

    /// <summary>Thumbnail image paths for the filmstrip (generated lazily by the service).</summary>
    public List<string> Thumbnails { get; } = new();

    public EditorSegment Clone() => new(SourceFile, InSec, OutSec);
}

public enum EditorOutputFormat
{
    Mp4,
    Gif
}

public enum EditorAudioMode
{
    Keep,
    Mute,
    Volume
}

/// <summary>Global options applied to the concatenated result on export.</summary>
public sealed class ExportOptions
{
    public EditorOutputFormat Format { get; set; } = EditorOutputFormat.Mp4;

    /// <summary>Output canvas size (both even). Segments are scaled+letterboxed to fit this exactly so
    /// they concatenate cleanly even when sources differ. For "keep" the UI passes the primary size.</summary>
    public int OutWidth { get; set; } = 1920;
    public int OutHeight { get; set; } = 1080;

    /// <summary>Manual rotation applied on export, on top of the source's own (auto-applied) orientation:
    /// 0 = none, 90 = clockwise, 180, 270 = counter-clockwise. The UI swaps OutWidth/OutHeight for 90/270.</summary>
    public int RotateDegrees { get; set; }

    public int OutFps { get; set; } = 60;

    /// <summary>When set, the encoder targets this file size (we compute the bitrate from total duration).
    /// When null, we encode at a quality default (CRF).</summary>
    public double? TargetSizeMb { get; set; }

    public EditorAudioMode AudioMode { get; set; } = EditorAudioMode.Keep;
    public double Volume { get; set; } = 1.0;

    public int GifFps { get; set; } = 15;

    /// <summary>Fast lossless cut (stream copy, keyframe-snapped). Only valid for MP4 with no resize,
    /// no target size and audio kept; the service falls back to a re-encode otherwise.</summary>
    public bool FastCopy { get; set; }
}
