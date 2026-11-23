// Desktop Snow
// Copyright (C) 2026 TonyAldo
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;

enum SnowAnimal { Rabbit, Fox, Penguin }

sealed class SnowAnimalScene
{
    internal sealed class PawPrint
    {
        public float X, Y, Age;
        public int Side, Direction;
    }

    struct Leg
    {
        public PointF Start, End;
        public bool Falling;
    }

    readonly Random random = new Random();
    readonly List<DisplayArea> layout = new List<DisplayArea>();
    readonly List<Leg> route = new List<Leg>();
    public readonly List<PawPrint> Prints = new List<PawPrint>();
    public bool Active { get; private set; }
    public bool Falling { get; private set; }
    public SnowAnimal Visitor { get; private set; }
    public int Direction { get; private set; }
    public float X { get; private set; }
    public float Y { get; private set; }
    public float Gait { get; private set; }
    public float Lift { get; private set; }
    public float Slide { get; private set; }
    int legIndex, pawSide;
    float progress, trailX, nextVisit;

    public void Advance(float dt, List<DisplayArea> displays)
    {
        if (!MatchesLayout(displays))
        {
            layout.Clear();
            layout.AddRange(displays);
            Active = false;
            Prints.Clear();
            nextVisit = 1.6f + (float)random.NextDouble() * 3f;
        }
        if (layout.Count == 0) return;

        for (int i = Prints.Count - 1; i >= 0; i--)
        {
            Prints[i].Age += dt;
            if (Prints[i].Age >= 3.2f) Prints.RemoveAt(i);
        }
        if (!Active)
        {
            nextVisit -= dt;
            if (nextVisit <= 0)
                StartVisit(layout[random.Next(layout.Count)], (SnowAnimal)random.Next(3),
                    random.Next(2) == 0 ? -1 : 1);
        }
        if (Active) Walk(dt);
    }

    bool MatchesLayout(List<DisplayArea> displays)
    {
        if (layout.Count != displays.Count) return false;
        for (int i = 0; i < displays.Count; i++)
            if (layout[i].Bounds != displays[i].Bounds || layout[i].WorkArea != displays[i].WorkArea) return false;
        return true;
    }

    void Walk(float dt)
    {
        Gait += dt;
        float beat = Visitor == SnowAnimal.Rabbit ? 2.6f : Visitor == SnowAnimal.Fox ? 2.15f : 2.9f;
        float stride = (float)Math.Sin(Gait * beat % 1 * Math.PI);
        float pounce = Gait % 3.4f;
        pounce = pounce < 2.8f ? 0 : (float)Math.Sin((pounce - 2.8f) / .6f * Math.PI);
        float glide = Gait % 3.5f;
        Slide = Visitor == SnowAnimal.Penguin && glide > 2.1f
            ? Math.Min(1, Math.Min(glide - 2.1f, 3.5f - glide) * 4) : 0;
        float speed = Visitor == SnowAnimal.Rabbit ? 115 + 37 * stride
            : Visitor == SnowAnimal.Fox ? 138 + 25 * stride + 34 * pounce : 81 + 11 * stride + 48 * Slide;
        Lift = Visitor == SnowAnimal.Rabbit ? stride * 13
            : Visitor == SnowAnimal.Fox ? stride * 4 + pounce * 12 : stride * 1.5f * (1 - Slide);

        while (dt > 0 && legIndex < route.Count)
        {
            Leg leg = route[legIndex];
            float dx = leg.End.X - leg.Start.X, dy = leg.End.Y - leg.Start.Y;
            float duration = leg.Falling ? Math.Max(.22f, (float)Math.Sqrt(2 * dy / 750))
                : Math.Max(.01f, (float)Math.Sqrt(dx * dx + dy * dy) / speed);
            float consumed = Math.Min(dt, (1 - progress) * duration);
            progress = Math.Min(1, progress + consumed / duration);
            dt -= consumed;
            Falling = leg.Falling;
            X = leg.Start.X + dx * progress;
            float vertical = leg.Falling ? progress * progress : progress * progress * (3 - 2 * progress);
            Y = leg.Start.Y + dy * vertical;
            if (!Falling && Math.Abs(dy) < 1 && Lift < 3
                && Math.Abs(X - trailX) > (Visitor == SnowAnimal.Rabbit ? 23 : 12))
            {
                trailX = X;
                Prints.Add(new PawPrint { X = X, Y = Y, Side = pawSide++ % 2, Direction = Direction });
            }
            if (progress >= .9999f)
            {
                legIndex++;
                progress = 0;
            }
        }

        if (legIndex < route.Count) return;
        Active = false;
        nextVisit = 11 + (float)random.NextDouble() * 16;
    }

    void StartVisit(DisplayArea seed, SnowAnimal kind, int direction)
    {
        // Start at the outer edge of a connected row, or on the upper display of a stack.
        var display = seed;
        DisplayArea neighbor;
        while ((neighbor = VerticalNeighbor(display, false)) != null) display = neighbor;
        while ((neighbor = SideNeighbor(display, -direction, null)) != null) display = neighbor;

        route.Clear();
        var visited = new HashSet<DisplayArea>();
        var point = new PointF(direction > 0 ? display.Bounds.Left - 38 : display.Bounds.Right + 38, display.Ground);
        BeginVisit(kind, direction, point);
        while (visited.Add(display))
        {
            var side = SideNeighbor(display, direction, visited);
            if (side != null)
            {
                int seam = direction > 0 ? display.Bounds.Right : display.Bounds.Left;
                int passHeight = Math.Min(display.Ground, side.Ground);
                float rise = 48 + Math.Abs(display.Ground - side.Ground) * .28f;
                float approach = Math.Min(rise, Math.Max(0, (seam - point.X) * direction) * .55f);
                float exit = Math.Min(rise, side.Bounds.Width * .30f);
                AddLeg(ref point, seam - direction * approach, display.Ground);
                AddLeg(ref point, seam, passHeight);
                AddLeg(ref point, seam + direction * exit, side.Ground);
                display = side;
                continue;
            }

            var lower = VerticalNeighbor(display, true);
            if (lower != null && !visited.Contains(lower))
            {
                int left = Math.Max(display.Bounds.Left, lower.Bounds.Left) + 38;
                int right = Math.Min(display.Bounds.Right, lower.Bounds.Right) - 38;
                float depart = direction > 0 ? right - 12 : left + 12;
                if ((depart - point.X) * direction >= 0)
                {
                    AddLeg(ref point, depart, display.Ground);
                    AddLeg(ref point, depart + 12 * direction, lower.Ground, true);
                    display = lower;
                    continue;
                }
            }

            AddLeg(ref point, direction > 0 ? display.Bounds.Right + 38 : display.Bounds.Left - 38, display.Ground);
            break;
        }
        Active = route.Count != 0;
    }

    DisplayArea SideNeighbor(DisplayArea from, int direction, HashSet<DisplayArea> visited)
    {
        DisplayArea best = null;
        int difference = int.MaxValue;
        foreach (var display in layout)
        {
            if (display == from || (visited != null && visited.Contains(display))) continue;
            if (direction > 0 ? from.Bounds.Right != display.Bounds.Left : from.Bounds.Left != display.Bounds.Right)
                continue;
            int seamGround = Math.Min(from.Ground, display.Ground);
            int seamTop = Math.Max(from.WorkArea.Top, display.WorkArea.Top);
            if (seamGround - seamTop < 55) continue;
            int offset = Math.Abs(from.Ground - display.Ground);
            if (offset >= difference) continue;
            best = display;
            difference = offset;
        }
        return best;
    }

    DisplayArea VerticalNeighbor(DisplayArea from, bool below)
    {
        DisplayArea best = null;
        int bestOverlap = 100;
        foreach (var display in layout)
        {
            if (display == from) continue;
            if (below ? from.Bounds.Bottom != display.Bounds.Top : from.Bounds.Top != display.Bounds.Bottom) continue;
            int overlap = Math.Min(from.Bounds.Right, display.Bounds.Right) - Math.Max(from.Bounds.Left, display.Bounds.Left);
            if (overlap <= bestOverlap) continue;
            best = display;
            bestOverlap = overlap;
        }
        return best;
    }

    void AddLeg(ref PointF point, float x, float y, bool falling = false)
    {
        var end = new PointF(x, y);
        if (point != end) route.Add(new Leg { Start = point, End = end, Falling = falling });
        point = end;
    }

    void BeginVisit(SnowAnimal kind, int direction, PointF start)
    {
        Visitor = kind;
        Direction = direction;
        X = start.X;
        Y = start.Y;
        trailX = X;
        Gait = Lift = Slide = progress = 0;
        legIndex = pawSide = 0;
        Falling = false;
    }

    internal void TestAt(int kind, float x, float y)
    {
        var point = new PointF(x, y);
        BeginVisit((SnowAnimal)kind, 1, point);
        route.Clear();
        AddLeg(ref point, x + 150, y);
        Active = true;
    }

    internal static void VerifyRoutes()
    {
        var left = new DisplayArea(new Rectangle(-400, -60, 400, 380), new Rectangle(-400, -60, 400, 360));
        var right = new DisplayArea(new Rectangle(0, 0, 500, 440), new Rectangle(0, 0, 500, 410));
        var scene = new SnowAnimalScene();
        var pair = new List<DisplayArea> { right, left };
        scene.Advance(0, pair);
        scene.StartVisit(right, SnowAnimal.Fox, 1);
        if (scene.X >= left.Bounds.Left)
            throw new InvalidOperationException("The route did not start on the outer display.");
        bool crossed = false;
        for (int i = 0; i < 400 && scene.Active; i++)
        {
            scene.Advance(.025f, pair);
            if (scene.X < 1 || scene.X > 18) continue;
            crossed = scene.Visitor == SnowAnimal.Fox && scene.Gait > 1 && scene.Y >= 295 && scene.Y < 330;
            if (crossed) break;
        }
        if (!crossed) throw new InvalidOperationException("The same animal did not cross staggered displays.");

        var upper = new DisplayArea(new Rectangle(0, -380, 480, 380), new Rectangle(0, -380, 480, 350));
        var stack = new List<DisplayArea> { right, upper };
        scene.Advance(0, stack);
        scene.StartVisit(right, SnowAnimal.Penguin, 1);
        crossed = false;
        for (int i = 0; i < 400 && scene.Active; i++)
        {
            scene.Advance(.025f, stack);
            if (scene.Y > right.Bounds.Top + 30 && scene.Falling && scene.Visitor == SnowAnimal.Penguin)
            {
                crossed = true;
                break;
            }
        }
        if (!crossed) throw new InvalidOperationException("The animal did not descend to the lower display.");
    }
}

partial class SnowWindow
{
    readonly SolidBrush fur = new SolidBrush(Color.FromArgb(248, 230, 236, 242));
    readonly SolidBrush darkFur = new SolidBrush(Color.FromArgb(245, 163, 178, 195));
    readonly SolidBrush pink = new SolidBrush(Color.FromArgb(249, 231, 165, 179));
    readonly SolidBrush foxFur = new SolidBrush(Color.FromArgb(252, 195, 80, 40));
    readonly SolidBrush foxShade = new SolidBrush(Color.FromArgb(252, 130, 49, 35));
    readonly SolidBrush penguinFur = new SolidBrush(Color.FromArgb(253, 36, 48, 62));
    readonly SolidBrush printInk = new SolidBrush(Color.Transparent);
    readonly SolidBrush powder = new SolidBrush(Color.Transparent);
    void DrawAnimals(SnowAnimalScene scene, List<Rectangle> windowBounds)
    {
        if (!scene.Active && scene.Prints.Count == 0) return;
        var monitor = ScreenBounds;
        var dirty = Rectangle.Empty;
        bool drawBody = scene.Active && monitor.IntersectsWith(
            new Rectangle((int)scene.X - 42, (int)scene.Y - 58, 84, 63));
        if (drawBody) dirty = new Rectangle((int)scene.X - 42, (int)scene.Y - 58, 84, 63);
        foreach (var print in scene.Prints)
        {
            var area = new Rectangle((int)print.X - 26, (int)print.Y - 18, 52, 20);
            if (area.IntersectsWith(monitor)) dirty = dirty.IsEmpty ? area : Rectangle.Union(dirty, area);
        }
        if (dirty.IsEmpty) return;

        var state = canvas.Save();
        var workArea = Display.WorkArea;
        workArea.Offset(-monitor.Left, -monitor.Top);
        canvas.SetClip(workArea, System.Drawing.Drawing2D.CombineMode.Intersect);
        dirty.Offset(-monitor.Left, -monitor.Top);
        foreach (var bounds in windowBounds)
            if (bounds.IntersectsWith(dirty)) canvas.ExcludeClip(bounds);
        canvas.TranslateTransform(-monitor.Left, -monitor.Top);

        foreach (var print in scene.Prints)
        {
            if (!monitor.IntersectsWith(new Rectangle((int)print.X - 26, (int)print.Y - 18, 52, 20))) continue;
            float y = print.Y - 1.5f - print.Side * 2;
            int alpha = (int)(90 * (1 - print.Age / 3.2f));
            printInk.Color = Color.FromArgb(alpha, 87, 132, 159);
            canvas.FillEllipse(printInk, print.X - 2, y, 4, 1.35f);
            if (print.Age >= .44f) continue;
            float t = print.Age / .44f;
            powder.Color = Color.FromArgb((int)(170 * (1 - t)), 240, 249, 255);
            for (int j = 0; j < 4; j++)
            {
                float x = print.X - print.Direction * t * (j * 4 + 5);
                float rise = 1 + (float)Math.Sin(t * Math.PI) * (3 + j * 1.5f);
                canvas.FillEllipse(powder, x, y - rise, 2.6f - j * .3f, 2.4f - j * .3f);
            }
        }

        if (drawBody)
        {
            if (!scene.Falling) canvas.FillEllipse(shadows[8], scene.X - 12, scene.Y - 1.5f, 24, 1.5f);
            canvas.TranslateTransform(scene.X, scene.Y - (scene.Falling ? 0 : scene.Lift));
            canvas.ScaleTransform(scene.Direction, 1);
            float swing = (float)Math.Sin(scene.Gait * 17);
            switch (scene.Visitor)
            {
                case SnowAnimal.Rabbit: DrawRabbit(swing); break;
                case SnowAnimal.Fox: DrawFox(swing); break;
                case SnowAnimal.Penguin: DrawPenguin(swing * (1 - scene.Slide), scene.Slide); break;
            }
        }
        canvas.Restore(state);
    }

    void DrawRabbit(float swing)
    {
        // Hind feet tuck forward at the top of a hop.
        canvas.FillEllipse(darkFur, -9 + swing * 4, -4, 11, 4);
        canvas.FillEllipse(darkFur, 4 - swing * 3, -4, 7, 4);
        canvas.FillEllipse(darkFur, -17, -13, 8, 8);
        canvas.FillEllipse(fur, -14, -14, 23, 14);
        canvas.FillEllipse(darkFur, 0, -30, 5, 16);
        var ears = canvas.Save();
        canvas.RotateTransform(-15);
        canvas.FillEllipse(fur, 8, -31, 5.5f, 17);
        canvas.FillEllipse(pink, 9.5f, -29, 2.3f, 11);
        canvas.Restore(ears);
        canvas.FillEllipse(fur, 1, -21, 15, 13);
        canvas.FillEllipse(snow, 9, -15, 7, 5);
        canvas.FillEllipse(tire, 10, -17, 1.9f, 1.9f);
        canvas.FillEllipse(pink, 14, -13, 2.2f, 1.8f);
    }

    void DrawFox(float swing)
    {
        canvas.FillClosedCurve(foxShade, new[] {
            new PointF(-11, -11), new PointF(-20, -18), new PointF(-30, -15),
            new PointF(-28, -9), new PointF(-18, -6), new PointF(-10, -7)
        });
        canvas.FillEllipse(fur, -30, -15, 7, 6);
        canvas.FillEllipse(foxShade, -10 + swing * 5, -8, 5, 8);
        canvas.FillEllipse(foxShade, 6 - swing * 5, -8, 5, 8);
        canvas.FillEllipse(foxFur, -14, -17, 27, 13);
        canvas.FillEllipse(fur, 6, -17, 9, 12);
        canvas.FillPolygon(foxShade, new[] { new PointF(5, -20), new PointF(4, -32), new PointF(12, -24) });
        canvas.FillPolygon(foxFur, new[] { new PointF(12, -22), new PointF(14, -31), new PointF(18, -23) });
        canvas.FillEllipse(foxFur, 4, -26, 16, 14);
        canvas.FillPolygon(fur, new[] { new PointF(12, -17), new PointF(24, -17), new PointF(18, -12), new PointF(12, -13) });
        canvas.FillEllipse(tire, 16, -21, 1.9f, 1.9f);
        canvas.FillEllipse(tire, 22, -18, 3, 2.5f);
    }

    void DrawPenguin(float swing, float slide)
    {
        canvas.FillEllipse(orange, -8 + swing * 4, -3, 9, 3);
        canvas.FillEllipse(amber, 3 - swing * 4, -3, 9, 3);
        var posture = canvas.Save();
        canvas.TranslateTransform(0, slide * 2);
        canvas.ScaleTransform(1 + slide * .30f, 1 - slide * .41f);
        canvas.RotateTransform(swing * 7 + slide * 48);
        canvas.FillEllipse(penguinFur, -12, -27, 23, 25);
        canvas.FillEllipse(fur, -6, -21, 17, 18);
        canvas.FillEllipse(penguinFur, -8, -33, 19, 17);
        canvas.FillEllipse(fur, 3, -28, 7, 8);
        canvas.FillEllipse(tire, 6, -27, 2, 2);
        canvas.FillPolygon(amber, new[] { new PointF(9, -26), new PointF(17, -23), new PointF(9, -21) });
        canvas.FillEllipse(tire, -13 - swing * 1.5f, -21, 8, 13);
        canvas.Restore(posture);
    }

    internal static void VerifyAdjacentAnimalFrames()
    {
        var leftBounds = new Rectangle(-320, -36, 320, 300);
        var rightBounds = new Rectangle(0, -36, 400, 300);
        using (var left = new SnowWindow(new DisplayArea(leftBounds, leftBounds), true, handle => null))
        using (var right = new SnowWindow(new DisplayArea(rightBounds, rightBounds), true, handle => null))
        {
            var scene = new SnowAnimalScene();
            scene.TestAt((int)SnowAnimal.Penguin, 0, left.Display.Ground);
            var covers = new List<Rectangle>();
            left.BeginFrame(Rectangle.Empty);
            right.BeginFrame(Rectangle.Empty);
            left.DrawAnimals(scene, covers);
            right.DrawAnimals(scene, covers);
            left.canvas.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
            right.canvas.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
            int sampleY = left.Display.Ground - leftBounds.Top - 12;
            if (left.bitmap.GetPixel(leftBounds.Width - 6, sampleY).A == 0
                || right.bitmap.GetPixel(5, sampleY).A == 0)
                throw new InvalidOperationException("A display seam clipped one half of the same animal.");
            left.PublishOverlay();
            right.PublishOverlay();
        }
    }

    public void VerifyAnimalClip()
    {
        var scene = new SnowAnimalScene();
        int x = size.Width / 2;
        int ground = Display.Ground - ScreenBounds.Top;
        scene.TestAt((int)SnowAnimal.Penguin, ScreenBounds.Left + x, Display.Ground);
        var bounds = new List<Rectangle>();
        BeginFrame(Rectangle.Empty);
        DrawAnimals(scene, bounds);
        canvas.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        if (bitmap.GetPixel(x, ground - 12).A == 0)
            throw new InvalidOperationException("The test animal was not visible.");

        bounds.Add(new Rectangle(x - 40, ground - 50, 80, 55));
        BeginFrame(Rectangle.Empty);
        DrawAnimals(scene, bounds);
        canvas.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        if (bitmap.GetPixel(x, ground - 12).A != 0)
            throw new InvalidOperationException("The animal drew over an overlapping window.");

        bounds.Clear();
        BeginFrame(Rectangle.Empty);
        DrawAnimals(scene, bounds);
        canvas.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        if (bitmap.GetPixel(x, ground - 12).A == 0)
            throw new InvalidOperationException("The animal did not reappear after the window moved.");
    }

    void DisposeAnimals()
    {
        fur.Dispose();
        darkFur.Dispose();
        pink.Dispose();
        foxFur.Dispose();
        foxShade.Dispose();
        penguinFur.Dispose();
        printInk.Dispose();
        powder.Dispose();
    }
}



