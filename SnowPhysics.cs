// Desktop Snow
// Copyright (C) 2026 TonyAldo
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;

sealed partial class SnowWindow
{
    void Reset(Flake flake, bool scatter, SnowWeather weather)
    {
        flake.X = (float)random.NextDouble() * size.Width;
        flake.Y = scatter ? (float)random.NextDouble() * size.Height : -5 - random.Next(80);
        double depth = random.NextDouble();
        flake.Sprite = random.Next(2) + (depth < .44 ? 0 : depth < .93 ? 2 : 4);
        flake.Size = weather.Size * (1.7f + (float)Math.Pow(depth, 2.6) * 6.4f);
        flake.Speed = 18 + flake.Size * 6.6f + random.Next(15);
        flake.Phase = (float)random.NextDouble() * 6.28f;
        flake.Flutter = .75f + (float)random.NextDouble() * 2.4f;
    }

    public void BeginSweep(bool smoke)
    {
        sweeping = true;
        testSweep = smoke;
        SweepFinished = false;
        routeIndex = 0;
        ResetPlowTrack();
        route.Clear();
        foreach (var bank in banks)
        {
            bank.Breaks.Clear();
            if (bank.Occupied) route.Add(bank);
        }
        route.Sort((left, right) => right.Edge.Top.CompareTo(left.Edge.Top));
        if (route.Count == 0) route.Add(emptyRunway);
    }

    void ResetPlowTrack()
    {
        travel = 0;
        clearedBins = 0;
        mound = 0;
    }

    public void EndSweep()
    {
        sweeping = false;
        route.Clear();
        loose.Clear();
    }

    List<Rectangle> CollectWindowBounds(List<Ledge> ledges)
    {
        var windowBounds = new List<Rectangle>();
        foreach (Ledge ledge in ledges)
        {
            if (!ledge.Moves || ledge.Transient || !ledge.Bounds.IntersectsWith(ScreenBounds)) continue;
            var bounds = ledge.Bounds;
            bounds.Offset(-ScreenBounds.Left, -ScreenBounds.Top);
            windowBounds.Add(bounds);
        }
        return windowBounds;
    }

    void SyncBanks(List<Ledge> ledges, List<Rectangle> windowBounds, bool settle, float time)
    {
        foreach (var bank in banks) bank.Seen = false;
        if (settle)
        {
            var covers = new List<Rectangle>();
            bool bottomTaskbar = false;
            foreach (Ledge ledge in ledges)
            {
                if (ledge.Transient) continue;
                Rectangle bounds = ledge.Bounds;
                bounds.Offset(-ScreenBounds.Left, -ScreenBounds.Top);
                if (!bounds.IntersectsWith(new Rectangle(0, 0, size.Width, size.Height))) continue;
                if (!ledge.Moves && bounds.Left <= 0 && bounds.Right >= size.Width && bounds.Bottom >= size.Height)
                    bottomTaskbar = true;
                if ((ledge.Collects || !ledge.Moves) && bounds.Top >= 6 && bounds.Top < size.Height)
                    TrackBank(ledge.Handle, new Rectangle(bounds.Left, bounds.Top, bounds.Width, 1),
                        ledge.Moves ? covers : windowBounds, ledge.Moves, time, ledge.Moves);
                covers.Add(bounds);
            }
            if (!bottomTaskbar)
                TrackBank(IntPtr.Zero, new Rectangle(0, size.Height - 1, size.Width, 1), null, false, time);
        }
        if (settle)
            foreach (var bank in banks)
                if (!bank.Seen && bank.WindowLedge && Native.IsIconic(bank.Handle)) DropSnowbank(bank, time);
        banks.RemoveAll(bank => !bank.Seen);
    }

    void TrackBank(IntPtr handle, Rectangle edge, List<Rectangle> covers, bool moves, float time, bool shareDepth = false)
    {
        var bank = banks.Find(item => item.Handle == handle);
        if (bank == null)
        {
            WindowSnowDepth depth = null;
            if (shareDepth && windowSnow != null && !windowSnow.TryGetValue(handle, out depth))
            {
                depth = new WindowSnowDepth((edge.Width + BinWidth - 1) / BinWidth);
                windowSnow.Add(handle, depth);
            }
            bank = new Snowbank(edge, depth) { Handle = handle };
            banks.Add(bank);
        }
        int dx = edge.X - bank.MotionOrigin.X;
        int dy = edge.Y - bank.MotionOrigin.Y;
        if (moves && (Math.Abs(dx) >= 3 || Math.Abs(dy) >= 3))
        {
            int direction = Math.Abs(dx) >= Math.Abs(dy) ? Math.Sign(dx) : 2 * Math.Sign(dy);
            bool reversed = direction == -bank.Direction && time - bank.LastMove < .25f && bank.Motion >= 7;
            if (!sweeping && reversed && time - bank.LastShake > .12f)
            {
                QueueSnowBreaks(bank, dx, dy, time);
                bank.LastShake = time;
            }
            bank.Motion = bank.Direction == direction && time - bank.LastMove < .20f
                ? Math.Min(100, bank.Motion + Math.Abs(dx) + Math.Abs(dy)) : Math.Abs(dx) + Math.Abs(dy);
            bank.Direction = direction;
            bank.LastMove = time;
            bank.MotionOrigin = edge.Location;
        }
        int count = (edge.Width + BinWidth - 1) / BinWidth;
        if (count != bank.Heights.Length) Array.Resize(ref bank.Depth.Heights, count);
        bank.Edge = edge;
        bank.Taskbar = !moves && handle != IntPtr.Zero;
        bank.WindowLedge = moves;
        bank.Seen = true;
        bank.Covered.Clear();
        if (covers != null)
        {
            var strip = new Rectangle(edge.Left, edge.Top - 38, edge.Width, 42);
            foreach (var cover in covers)
                if (cover.IntersectsWith(strip)) bank.Covered.Add(cover);
        }
        if (!sweeping) ReleaseSnowBreaks(bank, time);
    }

    public void DropMinimizedSnow(IntPtr handle, float time)
    {
        var bank = banks.Find(item => item.Handle == handle && item.WindowLedge);
        if (bank == null) return;
        DropSnowbank(bank, time);
        banks.Remove(bank);
    }

    void DropSnowbank(Snowbank bank, float time)
    {
        bank.Breaks.Clear();
        QueueSnowBreaks(bank, 0, 0, time, true);
        ReleaseSnowBreaks(bank, time, true);
    }

    int BinAtBoundary(Snowbank bank, int x)
    {
        if (x <= bank.Edge.Left) return 0;
        if (x >= bank.Edge.Right) return bank.Heights.Length;
        return Math.Min(bank.Heights.Length, Math.Max(0, (int)Math.Ceiling((x - bank.Edge.Left) / (double)BinWidth - .5)));
    }

    void QueueSnowBreaks(Snowbank bank, int dx, int dy, float time, bool entireBank = false)
    {
        if (!bank.Occupied || bank.Breaks.Count != 0) return;
        int start = bank.Shared ? BinAtBoundary(bank, 0) : 0;
        int end = bank.Shared ? BinAtBoundary(bank, size.Width) : bank.Heights.Length;
        for (int i = start; i < end;)
        {
            if (bank.Heights[i] < (entireBank ? .2f : 1f))
            {
                i++;
                continue;
            }
            int count = Math.Min(random.Next(4, 13), end - i);
            bank.Breaks.Add(new SnowBreak
            {
                First = i, Count = count,
                Due = bank.Breaks.Count == 0 ? time : time + .025f + (float)random.NextDouble() * .19f,
                Vx = Math.Max(-175, Math.Min(175, dx * 11)),
                Vy = Math.Max(-80, Math.Min(85, dy * 8))
            });
            i += count + (entireBank ? 0 : random.Next(2, 10));
        }
    }

    void ReleaseSnowBreaks(Snowbank bank, float time, bool entireBank = false)
    {
        bool changed = false;
        for (int i = bank.Breaks.Count - 1; i >= 0; i--)
        {
            var piece = bank.Breaks[i];
            if (!entireBank && piece.Due > time) continue;
            bank.Breaks.RemoveAt(i);
            int start = bank.Shared ? Math.Max(piece.First, BinAtBoundary(bank, 0)) : piece.First;
            int limit = bank.Shared ? BinAtBoundary(bank, size.Width) : bank.Heights.Length;
            int end = Math.Min(limit, piece.First + piece.Count);
            float mass = 0, moment = 0, peak = 0;
            for (int bin = start; bin < end; bin++)
            {
                float height = bank.Heights[bin];
                float removed = entireBank ? height : height * (.37f + (float)random.NextDouble() * .32f);
                bank.Heights[bin] -= removed;
                mass += removed;
                moment += removed * bin;
                peak = Math.Max(peak, height);
            }
            if (mass <= .3f) continue;
            changed = true;

            float x = bank.Edge.Left + (moment / mass + .5f) * BinWidth;
            float y = bank.Edge.Top - peak * .7f;
            if (x < -10 || x > size.Width + 10 || Covered(bank, x, y)) continue;
            int fragments = Math.Min(4, 1 + (int)(mass / 13));
            for (int part = 0; part < fragments && loose.Count < 900; part++)
            {
                float span = piece.Count * BinWidth * .65f;
                loose.Add(new FallingSnow
                {
                    X = x + ((float)random.NextDouble() - .5f) * span,
                    Y = y + random.Next(-4, 5),
                    Vx = piece.Vx + random.Next(-75, 76),
                    Vy = entireBank ? random.Next(8, 49) : piece.Vy + random.Next(-40, 37),
                    Delay = entireBank ? Math.Max(0, piece.Due - time) : 0,
                    Radius = Math.Min(7, (1.2f + peak * .29f) * (.45f + (float)random.NextDouble() * .55f))
                });
            }
        }
        if (changed) bank.Occupied = Array.Exists(bank.Heights, height => height >= .2f);
    }

    static bool Covered(Snowbank bank, float x, float y)
    {
        foreach (var cover in bank.Covered)
            if (cover.Contains((int)x, (int)y)) return true;
        return false;
    }

    Snowbank AdvancePlow(float dt)
    {
        while (routeIndex < route.Count)
        {
            var track = route[routeIndex];
            int width = Math.Min(size.Width, track.Edge.Right) - Math.Max(0, track.Edge.Left);
            if (width > 0 && (track == emptyRunway || banks.Contains(track)))
            {
                int start = track.Shared ? BinAtBoundary(track, 0) : 0;
                int end = track.Shared ? BinAtBoundary(track, size.Width) : track.Heights.Length;
                clearedBins = Math.Max(start, clearedBins);
                int ahead = Math.Max(0, Math.Min(end - 1, clearedBins + 2));
                float resistance = Math.Min(1, (track.Heights[ahead] + mound * .25f) / 23f);
                float speed = testSweep ? (width + 65) * 5.5f : 210 - 115 * resistance;
                travel += speed * dt;
                int left = Math.Max(0, track.Edge.Left);
                float blade = left - 6 + travel;
                bool done = travel >= width + 65;
                float removed = 0;
                while (clearedBins < end
                    && (done || track.Edge.Left + clearedBins * BinWidth <= blade))
                {
                    float height = track.Heights[clearedBins];
                    float dust = Math.Min(height, .26f + .17f * (clearedBins % 7) / 6f);
                    track.Heights[clearedBins++] = dust;
                    removed += height - dust;
                }
                mound = Math.Min(29, mound * (float)Math.Exp(-dt * 3.4f) + removed * .27f);
                if (!done) return track;
                track.Occupied = Array.Exists(track.Heights, height => height >= .20f);
            }
            routeIndex++;
            ResetPlowTrack();
        }
        SweepFinished = true;
        return null;
    }

    Snowbank FindLanding(float center, float before, float after, out int bin)
    {
        Snowbank landing = null;
        bin = 0;
        float highest = float.MaxValue;
        foreach (var bank in banks)
        {
            if (center < bank.Edge.Left || center >= bank.Edge.Right) continue;
            int index = (int)(center - bank.Edge.Left) / BinWidth;
            float top = bank.Edge.Top - bank.Heights[index];
            if (before >= top || after < top || top >= highest || Covered(bank, center, top)) continue;
            landing = bank;
            bin = index;
            highest = top;
        }
        return landing;
    }

    static void Deposit(Snowbank bank, int bin, float amount)
    {
        if (amount <= 0) return;
        float limit = Math.Min(24, bank.Edge.Top - 2);
        for (int offset = -4; offset <= 4; offset++)
        {
            int index = bin + offset;
            if (index < 0 || index >= bank.Heights.Length) continue;
            float weight = 1 - Math.Abs(offset) / 5f;
            bank.Heights[index] = Math.Min(limit, bank.Heights[index] + amount * weight * weight);
        }
        bank.Occupied = true;
    }

    bool AdvanceClump(FallingSnow clump, float dt, float breeze, bool catchesSnow)
    {
        if (clump.Delay > 0)
        {
            clump.Delay -= dt;
            if (clump.Delay > 0) return true;
            dt = -clump.Delay;
            clump.Delay = 0;
        }
        float oldBottom = clump.Y + clump.Radius;
        clump.X += clump.Vx * dt;
        var bank = clump.Surface;
        float center = clump.X + clump.Radius * .5f;
        if (bank != null && catchesSnow && banks.Contains(bank)
            && center >= bank.Edge.Left && center < bank.Edge.Right && !Covered(bank, center, bank.Edge.Top - 2))
        {
            int index = (int)(center - bank.Edge.Left) / BinWidth;
            clump.Vx *= (float)Math.Exp(-4.5f * dt);
            float shed = Math.Min(clump.Radius, dt * (3.8f + Math.Abs(clump.Vx) * .026f));
            Deposit(bank, index, shed * .8f);
            clump.Radius -= shed;
            clump.Y = bank.Edge.Top - bank.Heights[index] - clump.Radius;
            return clump.Radius > .55f;
        }

        clump.Surface = null;
        clump.Vy += 360 * dt;
        clump.Vx += (breeze * .32f - clump.Vx * .65f) * dt;
        clump.Y += clump.Vy * dt;
        int bin = 0;
        bank = catchesSnow ? FindLanding(center, oldBottom, clump.Y + clump.Radius, out bin) : null;
        if (bank != null)
        {
            Deposit(bank, bin, clump.Radius * .44f);
            clump.Radius *= .66f;
            clump.Y = bank.Edge.Top - bank.Heights[bin] - clump.Radius;
            clump.Vy = 0;
            clump.Surface = bank;
        }
        return clump.Radius > .55f && clump.Y <= size.Height + 12 && clump.X > -120 && clump.X < size.Width + 120;
    }
}
