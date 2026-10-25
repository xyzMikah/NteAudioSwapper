using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Point = System.Windows.Point;
using Size  = System.Windows.Size;

namespace NtePakTool
{
    /// <summary>
    /// WrapPanel that slides cards smoothly to their new spot whenever the
    /// layout changes (window resize, search, filter chips). New cards fade in.
    /// Positions are remembered per song (DataContext), so even when the
    /// ItemsControl rebuilds its containers after a filter, cards still glide.
    /// </summary>
    public class AnimatedWrapPanel : WrapPanel
    {
        private static readonly Duration MoveTime = new(TimeSpan.FromMilliseconds(320));
        private static readonly Duration FadeTime = new(TimeSpan.FromMilliseconds(260));
        private static readonly IEasingFunction Ease = new QuarticEase { EasingMode = EasingMode.EaseOut };

        private readonly Dictionary<object, Point> _lastPos = new();
        private bool _firstLayoutDone;

        protected override Size ArrangeOverride(Size finalSize)
        {
            Size result = base.ArrangeOverride(finalSize);

            var seen = new HashSet<object>();
            int newIndex = 0;

            foreach (UIElement child in InternalChildren)
            {
                object key = (child as FrameworkElement)?.DataContext ?? child;
                seen.Add(key);

                Vector off = VisualTreeHelper.GetOffset(child);
                var now = new Point(off.X, off.Y);

                if (child.RenderTransform is not TranslateTransform tt || tt.IsFrozen)
                {
                    tt = new TranslateTransform();
                    child.RenderTransform = tt;
                }

                if (_lastPos.TryGetValue(key, out Point prev))
                {
                    // Where the card is on screen right now (old slot + any running slide)
                    double fromX = prev.X + tt.X - now.X;
                    double fromY = prev.Y + tt.Y - now.Y;
                    if (Math.Abs(fromX) > 0.5 || Math.Abs(fromY) > 0.5)
                    {
                        Slide(tt, TranslateTransform.XProperty, fromX, TimeSpan.Zero);
                        Slide(tt, TranslateTransform.YProperty, fromY, TimeSpan.Zero);
                    }
                }
                else
                {
                    // Brand new card: pop in (staggered on first open for a nice cascade)
                    var delay = _firstLayoutDone
                        ? TimeSpan.Zero
                        : TimeSpan.FromMilliseconds(Math.Min(newIndex * 18, 400));
                    newIndex++;

                    child.Opacity = 0;
                    child.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(0, 1, FadeTime) { BeginTime = delay, EasingFunction = Ease });
                    Slide(tt, TranslateTransform.XProperty, 0, delay);
                    Slide(tt, TranslateTransform.YProperty, 14, delay);
                }

                _lastPos[key] = now;
            }

            // Forget songs that are no longer shown, so they fade back in later
            var gone = new List<object>();
            foreach (var k in _lastPos.Keys) if (!seen.Contains(k)) gone.Add(k);
            foreach (var k in gone) _lastPos.Remove(k);

            if (InternalChildren.Count > 0) _firstLayoutDone = true;
            return result;
        }

        private static void Slide(TranslateTransform tt, DependencyProperty dp, double from, TimeSpan delay)
        {
            var anim = new DoubleAnimation(from, 0, MoveTime) { EasingFunction = Ease, BeginTime = delay };
            tt.BeginAnimation(dp, anim);
        }
    }
}
