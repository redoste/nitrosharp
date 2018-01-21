﻿using NitroSharp.Graphics;
using NitroSharp.NsScript;
using NitroSharp.Foundation;
using NitroSharp.Foundation.Animation;
using System;
using System.Numerics;
using NitroSharp.Animation;
using NitroSharp.Foundation.Graphics;

namespace NitroSharp
{
    public sealed partial class NitroCore
    {
        public override void Fade(string entityName, TimeSpan duration, NsRational opacity, NsEasingFunction easingFunction, TimeSpan delay)
        {
            foreach (var entity in _entities.Query(entityName))
            {
                FadeCore(entity, duration, opacity, easingFunction);
            }

            if (delay > TimeSpan.Zero)
            {
                Interpreter.SuspendThread(CurrentThread, delay);
            }
        }

        private static void FadeCore(Entity entity, TimeSpan duration, NsRational opacity, NsEasingFunction easingFunction)
        {
            var existingAnimation = entity.GetComponent<FadeAnimation>();
            if (existingAnimation != null)
            {
                entity.RemoveComponent(existingAnimation);
            }

            float adjustedOpacity = opacity.Rebase(1.0f);
            var visual = entity.GetComponent<Visual>();
            if (visual != null)
            {
                if (duration > TimeSpan.Zero)
                {
                    var fn = (TimingFunction)easingFunction;
                    var animation = new FadeAnimation(visual, visual.Opacity, adjustedOpacity, duration, fn);
                    entity.AddComponent(animation);
                }
                else
                {
                    visual.Opacity = adjustedOpacity;
                }
            }
        }

        public override void Move(string entityName, TimeSpan duration, NsCoordinate x, NsCoordinate y, NsEasingFunction easingFunction, TimeSpan delay)
        {
            foreach (var entity in _entities.Query(entityName))
            {
                MoveCore(entity, duration, x, y, easingFunction);
            }

            if (delay > TimeSpan.Zero)
            {
                Interpreter.SuspendThread(CurrentThread, delay);
            }
        }

        private static void MoveCore(Entity entity, TimeSpan duration, NsCoordinate x, NsCoordinate y, NsEasingFunction easingFunction)
        {
            var existingAnimation = entity.GetComponent<MoveAnimation>();
            if (existingAnimation != null)
            {
                entity.RemoveComponent(existingAnimation);
            }
            
            float targetX = x.Origin == NsCoordinateOrigin.CurrentValue ? entity.Transform.Margin.X + x.Value : x.Value;
            float targetY = y.Origin == NsCoordinateOrigin.CurrentValue ? entity.Transform.Margin.Y + y.Value : y.Value;
            Vector2 destination = new Vector2(targetX, targetY);

            if (duration > TimeSpan.Zero)
            {
                var fn = (TimingFunction)easingFunction;
                var animation = new MoveAnimation(entity.Transform, entity.Transform.Margin, destination, duration, fn);
                entity.AddComponent(animation);
            }
            else
            {
                entity.Transform.Margin = destination;
            }
        }

        public override void Zoom(string entityName, TimeSpan duration, NsRational scaleX, NsRational scaleY, NsEasingFunction easingFunction, TimeSpan delay)
        {
            foreach (var entity in _entities.Query(entityName))
            {
                ZoomCore(entity, duration, scaleX, scaleY, easingFunction);
            }

            if (delay > TimeSpan.Zero)
            {
                Interpreter.SuspendThread(CurrentThread, delay);
            }
        }

        private static void ZoomCore(Entity entity, TimeSpan duration, NsRational scaleX, NsRational scaleY, NsEasingFunction easingFunction)
        {
            var existingAnimation = entity.GetComponent<ZoomAnimation>();
            if (existingAnimation != null)
            {
                entity.RemoveComponent(existingAnimation);
            }
            
            scaleX = scaleX.Rebase(1.0f);
            scaleY = scaleY.Rebase(1.0f);

            if (duration > TimeSpan.Zero)
            {
                Vector2 initialScale = entity.Transform.Scale;
                Vector2 finalScale = new Vector2(scaleX, scaleY);
                if (initialScale == finalScale)
                {
                    entity.Transform.Scale = new Vector2(0.0f, 0.0f);
                }

                var fn = (TimingFunction)easingFunction;
                var animation = new ZoomAnimation(entity.Transform, initialScale, finalScale, duration, fn);
                entity.AddComponent(animation);
            }
            else
            {
                entity.Transform.Scale = new Vector2(scaleX, scaleY);
            }
        }

        public override void DrawTransition(string sourceEntityName, TimeSpan duration, NsRational initialOpacity,
            NsRational finalOpacity, NsRational feather, NsEasingFunction easingFunction, string maskFileName, TimeSpan delay)
        {
            initialOpacity = initialOpacity.Rebase(1.0f);
            finalOpacity = finalOpacity.Rebase(1.0f);

            initialOpacity = initialOpacity.Rebase(1.0f);
            finalOpacity = finalOpacity.Rebase(1.0f);

            foreach (var entity in _entities.Query(sourceEntityName))
            {
                SetupTransition(entity, duration, initialOpacity, finalOpacity, feather, easingFunction, maskFileName);
            }

            if (delay > TimeSpan.Zero)
            {
                Interpreter.SuspendThread(CurrentThread, delay);
            }
        }

        private void SetupTransition(Entity entity, TimeSpan duration, NsRational initialOpacity,
            NsRational finalOpacity, NsRational feather, NsEasingFunction easingFunction, string maskFileName)
        {
            var visual = entity.GetComponent<Visual>();
            if (visual != null)
            {
                var transitionSource = visual is Sprite sprite ?
                    (FadeTransition.IPixelSource)new FadeTransition.ImageSource(_content.Get<Texture2D>(sprite.Source.Id))
                    : new FadeTransition.SolidColorSource(visual.Color);

                var transition = new FadeTransition(transitionSource, _content.Get<Texture2D>(maskFileName));
                transition.Priority = visual.Priority;
                Action<Component, float> propertySetter = (c, v) => (c as FadeTransition).Opacity = v;
                var animation = new FloatAnimation(transition, propertySetter, initialOpacity, finalOpacity, duration);

                animation.Completed += (o, args) =>
                {
                    if (visual is Sprite originalSprite)
                    {
                        originalSprite.Source = _content.Get<Texture2D>(originalSprite.Source.Id);
                    }

                    transition.Free(_game._renderSystem._renderer);
                    entity.RemoveComponent(transition);
                    entity.AddComponent(visual);
                };

                entity.RemoveComponent(visual);
                entity.AddComponent(transition);
                entity.AddComponent(animation);
            }
        }
    }
}
