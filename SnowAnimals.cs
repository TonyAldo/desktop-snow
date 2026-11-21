// Desktop Snow
// Copyright (C) 2026 TonyAldo
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

partial class SnowWindow
{
    enum Animal { Rabbit, Fox, Penguin }

    sealed class PawPrint
    {
        public float X, Age;
        public int Side;
    }

    readonly List<PawPrint> prints = new List<PawPrint>();
    readonly SolidBrush fur = new SolidBrush(Color.FromArgb(248, 230, 236, 242));
    readonly SolidBrush darkFur = new SolidBrush(Color.FromArgb(245, 163, 178, 195));
    readonly SolidBrush pink = new SolidBrush(Color.FromArgb(249, 231, 165, 179));
    readonly SolidBrush foxFur = new SolidBrush(Color.FromArgb(252, 195, 80, 40));
    readonly SolidBrush foxShade = new SolidBrush(Color.FromArgb(252, 130, 49, 35));
    readonly SolidBrush penguinFur = new SolidBrush(Color.FromArgb(253, 36, 48, 62));
    readonly SolidBrush printInk = new SolidBrush(Color.Transparent);
    readonly SolidBrush powder = new SolidBrush(Color.Transparent);
    bool animalActive, animalsEnabled;
    Animal visitor;
    float nextVisit, animalX, trailX, gait;
    int animalDirection, pawSide;
    int snowLine = -1;

    void ResetAnimals()
    {
        animalActive = false;
        prints.Clear();
        nextVisit = 1.6f + (float)random.NextDouble() * 3f;
    }

    void StartAnimal(Animal kind, int direction)
    {
        visitor = kind;
        animalDirection = direction;
        animalX = direction > 0 ? -36 : size.Width + 36;
        trailX = animalX;
        gait = 0;
        pawSide = 0;
        animalActive = true;
    }

    void DrawAnimals(float dt, bool enabled)
    {
        if (animalsEnabled != enabled)
        {
            animalsEnabled = enabled;
            ResetAnimals();
        }
        if (!enabled) return;

        // The working-area edge sits just above a bottom taskbar.
        int ground = Math.Min(size.Height - 1, Screen.FromRectangle(ScreenBounds).WorkingArea.Bottom - ScreenBounds.Top);
        if (ground < 35) ground = size.Height - 1;
        if (ground != snowLine)
        {
            prints.Clear();
            snowLine = ground;
        }

        if (!animalActive && (nextVisit -= dt) <= 0)
            StartAnimal((Animal)random.Next(3), random.Next(2) == 0 ? -1 : 1);

        float lift = 0;
        float slide = 0;
        if (animalActive)
        {
            gait += dt;
            float beat = visitor == Animal.Rabbit ? 2.6f : visitor == Animal.Fox ? 2.15f : 2.9f;
            float step = gait * beat % 1;
            float stride = (float)Math.Sin(step * Math.PI);
            // A fox pounces every few strides, and a penguin alternates waddling and belly-sliding.
            float pounce = gait % 3.4f;
            if (pounce < 2.8f) pounce = 0;
            else pounce = (float)Math.Sin((pounce - 2.8f) / .6f * Math.PI);
            float glide = gait % 3.5f;
            if (visitor == Animal.Penguin && glide > 2.1f)
                slide = Math.Min(1, Math.Min(glide - 2.1f, 3.5f - glide) * 4);
            float speed = visitor == Animal.Rabbit ? 115 + 37 * stride
                : visitor == Animal.Fox ? 138 + 25 * stride + 34 * pounce : 81 + 11 * stride + 48 * slide;
            lift = visitor == Animal.Rabbit ? stride * 13
                : visitor == Animal.Fox ? stride * 4 + pounce * 12 : stride * 1.5f * (1 - slide);
            animalX += speed * dt * animalDirection;
            if (animalX < -38 || animalX > size.Width + 38)
            {
                animalActive = false;
                nextVisit = 11 + (float)random.NextDouble() * 16;
            }
            else if (lift < 3 && Math.Abs(animalX - trailX) > (visitor == Animal.Rabbit ? 23 : 12))
            {
                trailX = animalX;
                prints.Add(new PawPrint { X = animalX, Side = pawSide++ % 2 });
            }
        }

        for (int i = prints.Count - 1; i >= 0; i--)
        {
            var print = prints[i];
            print.Age += dt;
            if (print.Age >= 3.2f)
            {
                prints.RemoveAt(i);
                continue;
            }
            float y = ground - 1.5f - print.Side * 2;
            int alpha = (int)(90 * (1 - print.Age / 3.2f));
            printInk.Color = Color.FromArgb(alpha, 87, 132, 159);
            canvas.FillEllipse(printInk, print.X - 2, y, 4, 1.35f);
            if (print.Age < .44f)
            {
                float t = print.Age / .44f;
                powder.Color = Color.FromArgb((int)(170 * (1 - t)), 240, 249, 255);
                for (int j = 0; j < 4; j++)
                {
                    float x = print.X - animalDirection * t * (j * 4 + 5);
                    float rise = 1 + (float)Math.Sin(t * Math.PI) * (3 + j * 1.5f);
                    canvas.FillEllipse(powder, x, y - rise, 2.6f - j * .3f, 2.4f - j * .3f);
                }
            }
        }

        if (!animalActive) return;
        canvas.FillEllipse(shadows[8], animalX - 12, ground - 1.5f, 24, 1.5f);
        var state = canvas.Save();
        canvas.TranslateTransform(animalX, ground - lift);
        canvas.ScaleTransform(animalDirection, 1);
        float swing = (float)Math.Sin(gait * 17);
        switch (visitor)
        {
            case Animal.Rabbit: DrawRabbit(swing); break;
            case Animal.Fox: DrawFox(swing); break;
            case Animal.Penguin: DrawPenguin(swing * (1 - slide), slide); break;
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

    internal void TestAnimalFrame(int kind)
    {
        animalsEnabled = true;
        StartAnimal((Animal)kind, kind % 2 == 0 ? 1 : -1);
        animalX = size.Width * .5f;
        trailX = animalX - 29 * animalDirection;
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
