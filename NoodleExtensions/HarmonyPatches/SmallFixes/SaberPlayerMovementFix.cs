﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CustomJSONData.CustomBeatmap;
using HarmonyLib;
using Heck;
using IPA.Utilities;
using SiraUtil.Affinity;
using UnityEngine;

namespace NoodleExtensions.HarmonyPatches.SmallFixes
{
    internal class SaberPlayerMovementFix : IAffinity, IDisposable
    {
        private static readonly FieldAccessor<PlayerTransforms, Transform>.Accessor _originAccessor =
            FieldAccessor<PlayerTransforms, Transform>.GetAccessor("_originTransform");

        private static readonly FieldInfo _topPos = AccessTools.Field(typeof(BladeMovementDataElement), nameof(BladeMovementDataElement.topPos));
        private static readonly FieldInfo _bottomPos = AccessTools.Field(typeof(BladeMovementDataElement), nameof(BladeMovementDataElement.bottomPos));

        private static readonly Dictionary<IBladeMovementData, SaberMovementData> _worldMovementData = new();

        private readonly Transform _origin;
        private readonly CodeInstruction _computeWorld;
        private readonly bool _local;

        private SaberPlayerMovementFix(PlayerTransforms playerTransforms, IReadonlyBeatmapData beatmapData)
        {
            _origin = _originAccessor(ref playerTransforms);
            _computeWorld = InstanceTranspilers.EmitInstanceDelegate<Func<Vector3, Vector3>>(ComputeWorld);
            _local = ((CustomBeatmapData)beatmapData).beatmapCustomData.Get<bool?>(NoodleController.TRAIL_LOCAL_SPACE) ?? false;
        }

        public void Dispose()
        {
            InstanceTranspilers.DisposeDelegate(_computeWorld);
        }

        [AffinityPrefix]
        [AffinityPatch(typeof(SaberTrail), nameof(SaberTrail.Setup))]
        private void CreateSaberMovementData(ref IBladeMovementData movementData, SaberTrail __instance, TrailRenderer ____trailRenderer)
        {
            if (_local)
            {
                Log.Logger.Log("Parented saber trail to local space.");
                ____trailRenderer.transform.SetParent(__instance.transform.parent.parent.parent, false);
                return;
            }

            // use world movement data for saber trail
            SaberMovementData world = new();
            _worldMovementData.Add(movementData, world);
            movementData = world;
        }

        [AffinityPostfix]
        [AffinityPatch(typeof(SaberTrail), nameof(SaberTrail.OnDestroy))]
        private void CleanupWorldMovement(IBladeMovementData ____movementData)
        {
            if (_local)
            {
                return;
            }

            _worldMovementData
                .Where(n => n.Value == ____movementData)
                .Select(n => n.Key)
                .ToArray()
                .Do(n => _worldMovementData.Remove(n));
        }

        // We store all positions as localpositions so that abrupt changes in world position do not affect this
        // it gets converted back to world position to calculate cut
        [AffinityPrefix]
        [AffinityPatch(typeof(SaberMovementData), nameof(SaberMovementData.AddNewData))]
        private void ConvertToWorld(SaberMovementData __instance, ref Vector3 topPos, ref Vector3 bottomPos, float time)
        {
            if (_local)
            {
                return;
            }

            if (_worldMovementData.ContainsValue(__instance))
            {
                return;
            }

            // fill world movement data with world position for saber
            if (_worldMovementData.TryGetValue(__instance, out SaberMovementData world))
            {
                world.AddNewData(topPos, bottomPos, time);
            }

            topPos = _origin.InverseTransformPoint(topPos);
            bottomPos = _origin.InverseTransformPoint(bottomPos);
        }

        [AffinityPostfix]
        [AffinityPatch(typeof(SaberMovementData), nameof(SaberMovementData.prevAddedData), AffinityMethodType.Getter)]
        [AffinityPatch(typeof(SaberMovementData), nameof(SaberMovementData.lastAddedData), AffinityMethodType.Getter)]
        private void ConvertToLocal(SaberMovementData __instance, ref BladeMovementDataElement __result)
        {
            if (_local)
            {
                return;
            }

            if (_worldMovementData.ContainsValue(__instance))
            {
                return;
            }

            __result.topPos = _origin.TransformPoint(__result.topPos);
            __result.bottomPos = _origin.TransformPoint(__result.bottomPos);
        }

        [AffinityPrefix]
        [AffinityPatch(typeof(SaberSwingRatingCounter), nameof(SaberSwingRatingCounter.ProcessNewData))]
        private void ConvertProcessorToLocal(ref BladeMovementDataElement newData, ref BladeMovementDataElement prevData)
        {
            if (_local)
            {
                return;
            }

            newData.topPos = _origin.TransformPoint(newData.topPos);
            newData.bottomPos = _origin.TransformPoint(newData.bottomPos);
            prevData.topPos = _origin.TransformPoint(prevData.topPos);
            prevData.bottomPos = _origin.TransformPoint(prevData.bottomPos);
        }

        [AffinityTranspiler]
        [AffinityPatch(typeof(SaberMovementData), nameof(SaberMovementData.ComputeAdditionalData))]
        private IEnumerable<CodeInstruction> ComputeWorldTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            if (_local)
            {
                return instructions;
            }

            return new CodeMatcher(instructions)
                /*
                 * -- Vector3 topPos2 = this._data[num2].topPos;
                 * -- Vector3 bottomPos2 = this._data[num2].bottomPos;
                 * -- Vector3 topPos3 = this._data[num3].topPos;
                 * -- Vector3 bottomPos3 = this._data[num3].bottomPos;
                 * ++ Vector3 topPos2 = ComputeWorld(this._data[num2].topPos);
                 * ++ Vector3 bottomPos2 = ComputeWorld(this._data[num2].bottomPos);
                 * ++ Vector3 topPos3 = ComputeWorld(this._data[num3].topPos);
                 * ++ Vector3 bottomPos3 = ComputeWorld(this._data[num3].bottomPos);
                 */
                .MatchForward(
                    false,
                    new CodeMatch(n => n.opcode == OpCodes.Ldfld && (ReferenceEquals(n.operand, _topPos) || ReferenceEquals(n.operand, _bottomPos))))
                .Repeat(n => n
                    .Advance(1)
                    .Insert(_computeWorld))
                .InstructionEnumeration();
        }

        private Vector3 ComputeWorld(Vector3 original)
        {
            return _origin.TransformPoint(original);
        }
    }
}
