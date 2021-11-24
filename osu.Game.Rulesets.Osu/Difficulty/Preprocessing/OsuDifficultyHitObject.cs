// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Objects;
using osuTK;

namespace osu.Game.Rulesets.Osu.Difficulty.Preprocessing
{
    public class OsuDifficultyHitObject : DifficultyHitObject
    {
        private const int normalised_radius = 50; // Change radius to 50 to make 100 the diameter. Easier for mental maths.
        private const int min_delta_time = 25;
        private const float maximum_slider_radius = normalised_radius * 2.4f;
        private const float assumed_slider_radius = normalised_radius * 1.8f;

        protected new OsuHitObject BaseObject => (OsuHitObject)base.BaseObject;

        /// <summary>
        /// Milliseconds elapsed since the start time of the previous <see cref="OsuDifficultyHitObject"/>, with a minimum of 25ms.
        /// </summary>
        public readonly double StrainTime;

        /// <summary>
        /// Normalised distance from the end position of the previous <see cref="OsuDifficultyHitObject"/> to the start position of this <see cref="OsuDifficultyHitObject"/>.
        /// </summary>
        public double JumpDistance { get; private set; }

        /// <summary>
        /// Normalised minimum distance from the end position of the previous <see cref="OsuDifficultyHitObject"/> to the start position of this <see cref="OsuDifficultyHitObject"/>.
        /// </summary>
        /// <remarks>
        /// This is bounded by <see cref="JumpDistance"/>, but may be smaller if a more natural path is able to be taken through a preceding slider.
        /// </remarks>
        public double MovementDistance { get; private set; }

        /// <summary>
        /// The time taken to travel through <see cref="MovementDistance"/>, with a minimum value of 25ms.
        /// </summary>
        public double MovementTime { get; private set; }

        /// <summary>
        /// Normalised distance between the start and end position of this <see cref="OsuDifficultyHitObject"/>.
        /// </summary>
        public double TravelDistance { get; private set; }

        /// <summary>
        /// The time taken to travel through <see cref="TravelDistance"/>, with a minimum value of 25ms for a non-zero distance.
        /// </summary>
        public double TravelTime { get; private set; }

        /// <summary>
        /// Angle the player has to take to hit this <see cref="OsuDifficultyHitObject"/>.
        /// Calculated as the angle between the circles (current-2, current-1, current).
        /// </summary>
        public double? Angle { get; private set; }

        private readonly OsuHitObject lastLastObject;
        private readonly OsuHitObject lastObject;

        public OsuDifficultyHitObject(HitObject hitObject, HitObject lastLastObject, HitObject lastObject, double clockRate)
            : base(hitObject, lastObject, clockRate)
        {
            this.lastLastObject = (OsuHitObject)lastLastObject;
            this.lastObject = (OsuHitObject)lastObject;

            // Capped to 25ms to prevent difficulty calculation breaking from simultaneous objects.
            StrainTime = Math.Max(DeltaTime, min_delta_time);

            setDistances(clockRate);
        }

        private void setDistances(double clockRate)
        {
            if (BaseObject is Slider currentSlider)
            {
                computeSliderCursorPosition(currentSlider);
                TravelDistance = currentSlider.LazyTravelDistance;
                TravelTime = Math.Max(currentSlider.LazyTravelTime / clockRate, min_delta_time);
            }

            // We don't need to calculate either angle or distance when one of the last->curr objects is a spinner
            if (BaseObject is Spinner || lastObject is Spinner)
                return;

            // We will scale distances by this factor, so we can assume a uniform CircleSize among beatmaps.
            float scalingFactor = normalised_radius / (float)BaseObject.Radius;

            if (BaseObject.Radius < 30)
            {
                float smallCircleBonus = Math.Min(30 - (float)BaseObject.Radius, 5) / 50;
                scalingFactor *= 1 + smallCircleBonus;
            }

            Vector2 lastCursorPosition = getEndCursorPosition(lastObject);

            JumpDistance = (BaseObject.StackedPosition * scalingFactor - lastCursorPosition * scalingFactor).Length;
            MovementTime = StrainTime;
            MovementDistance = JumpDistance;

            if (lastObject is Slider lastSlider)
            {
                double lastTravelTime = Math.Max(lastSlider.LazyTravelTime / clockRate, min_delta_time);
                MovementTime = Math.Max(StrainTime - lastTravelTime, min_delta_time);

                //
                // We'll try to better approximate the real movements a player will take in patterns following on from sliders. Consider the following slider-to-object patterns:
                //
                // 1.  <======o==>
                //            | /
                //            o
                //
                // 2.  <======o==>---o
                //            |______|
                //
                // Where "<==>" represents a slider, and "o" represents where the cursor needs to be for either hitobject (for a slider, this is the lazy cursor position).
                //
                // The pattern (o--o) has distance JumpDistance.
                // The pattern (>--o) is a new distance we'll call "tailJumpDistance".
                //
                // Case (1) is an anti-flow pattern, where players will cut the slider short in order to move to the next object. The most natural jump pattern is (o--o).
                // Case (2) is a flow pattern, where players will follow the slider through to its visual extent. The most natural jump pattern is (>--o).
                //
                // A lenience is applied by assuming that the player jumps the minimum of these two distances in all cases.
                //

                float tailJumpDistance = Vector2.Subtract(lastSlider.TailCircle.StackedPosition, BaseObject.StackedPosition).Length * scalingFactor;
                MovementDistance = Math.Max(0, Math.Min(JumpDistance - (maximum_slider_radius - assumed_slider_radius), tailJumpDistance - maximum_slider_radius));
            }

            if (lastLastObject != null && !(lastLastObject is Spinner))
            {
                Vector2 lastLastCursorPosition = getEndCursorPosition(lastLastObject);

                Vector2 v1 = lastLastCursorPosition - lastObject.StackedPosition;
                Vector2 v2 = BaseObject.StackedPosition - lastCursorPosition;

                float dot = Vector2.Dot(v1, v2);
                float det = v1.X * v2.Y - v1.Y * v2.X;

                Angle = Math.Abs(Math.Atan2(det, dot));
            }
        }

        private void computeSliderCursorPosition(Slider slider)
        {
            if (slider.LazyEndPosition != null)
                return;

            slider.LazyTravelTime = slider.NestedHitObjects[^1].StartTime - slider.StartTime;

            double endTimeMin = slider.LazyTravelTime / slider.SpanDuration;
            if (endTimeMin % 2 >= 1)
                endTimeMin = 1 - endTimeMin % 1;
            else
                endTimeMin %= 1;

            slider.LazyEndPosition = slider.StackedPosition + slider.Path.PositionAt(endTimeMin); // temporary lazy end position until a real result can be derived.
            var currCursorPosition = slider.StackedPosition;
            double scalingFactor = normalised_radius / slider.Radius; // lazySliderDistance is coded to be sensitive to scaling, this makes the maths easier with the thresholds being used.

            for (int i = 1; i < slider.NestedHitObjects.Count; i++)
            {
                var currMovementObj = (OsuHitObject)slider.NestedHitObjects[i];

                Vector2 currMovement = Vector2.Subtract(currMovementObj.StackedPosition, currCursorPosition);
                double currMovementLength = scalingFactor * currMovement.Length;

                // Amount of movement required so that the cursor position needs to be updated.
                double requiredMovement = assumed_slider_radius;

                if (i == slider.NestedHitObjects.Count - 1)
                {
                    // The end of a slider has special aim rules due to the relaxed time constraint on position.
                    // There is both a lazy end position as well as the actual end slider position. We assume the player takes the simpler movement.
                    // For sliders that are circular, the lazy end position may actually be farther away than the sliders true end.
                    // This code is designed to prevent buffing situations where lazy end is actually a less efficient movement.
                    Vector2 lazyMovement = Vector2.Subtract((Vector2)slider.LazyEndPosition, currCursorPosition);

                    if (lazyMovement.Length < currMovement.Length)
                        currMovement = lazyMovement;

                    currMovementLength = scalingFactor * currMovement.Length;
                }
                else if (currMovementObj is SliderRepeat)
                {
                    // For a slider repeat, assume a tighter movement threshold to better assess repeat sliders.
                    requiredMovement = normalised_radius;
                }

                if (currMovementLength > requiredMovement)
                {
                    // this finds the positional delta from the required radius and the current position, and updates the currCursorPosition accordingly, as well as rewarding distance.
                    currCursorPosition = Vector2.Add(currCursorPosition, Vector2.Multiply(currMovement, (float)((currMovementLength - requiredMovement) / currMovementLength)));
                    currMovementLength *= (currMovementLength - requiredMovement) / currMovementLength;
                    slider.LazyTravelDistance += (float)currMovementLength;
                }

                if (i == slider.NestedHitObjects.Count - 1)
                    slider.LazyEndPosition = currCursorPosition;
            }

            slider.LazyTravelDistance *= (float)Math.Pow(1 + slider.RepeatCount / 2.5, 1.0 / 2.5); // Bonus for repeat sliders until a better per nested object strain system can be achieved.
        }

        private Vector2 getEndCursorPosition(OsuHitObject hitObject)
        {
            Vector2 pos = hitObject.StackedPosition;

            if (hitObject is Slider slider)
            {
                computeSliderCursorPosition(slider);
                pos = slider.LazyEndPosition ?? pos;
            }

            return pos;
        }
    }
}
