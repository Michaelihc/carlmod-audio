using System;
using System.Globalization;
using UnityEngine;

namespace CarlModAudio.Internal;

/// <summary>Text formatting for command replies and logs.</summary>
internal static class AudioFormat
{
    public static string Time(TimeSpan time) =>
        time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";

    public static string Vector(Vector3 v) =>
        string.Format(CultureInfo.InvariantCulture, "({0:0.0}, {1:0.0}, {2:0.0})", v.x, v.y, v.z);

    public static string Percent(float volume) => $"{Mathf.RoundToInt(volume * 100f)}%";
}
