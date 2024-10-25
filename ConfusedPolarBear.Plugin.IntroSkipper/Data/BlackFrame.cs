// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// A frame of video that partially (or entirely) consists of black pixels.
/// </summary>
public class BlackFrame
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BlackFrame"/> class.
    /// </summary>
    /// <param name="percent">Percentage of the frame that is black.</param>
    /// <param name="time">Time this frame appears at.</param>
    public BlackFrame(int percent, double time)
    {
        Percentage = percent;
        Time = time;
    }

    /// <summary>
    /// Gets or sets the percentage of the frame that is black.
    /// </summary>
    public int Percentage { get; set; }

    /// <summary>
    /// Gets or sets the time (in seconds) this frame appeared at.
    /// </summary>
    public double Time { get; set; }
}
