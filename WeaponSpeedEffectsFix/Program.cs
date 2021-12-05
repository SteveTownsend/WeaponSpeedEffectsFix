using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DynamicData;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;

namespace WeaponSpeedEffectsFix
{
    public class Program
    {
        private static ModKey? WeaponEffectsSpeedModKey
        {
            get {
                try
                {
                    return ModKey.FromNameAndExtension("WeaponSpeedMultFix.esp");
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
        private static ModKey? AttackSpeedFrameworkModKey
        {
            get
            {
                try
                {
                    return ModKey.FromNameAndExtension("Attack Speed Framework.esp");
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        private static readonly string WSEFScript = "WeSpFiAVScript";
        private static readonly bool IgnoreMagnitude_1_0 = true;
        private static readonly float IgnoreMagnitudesStartingFrom = 10.0f;
        private static readonly float FloatEpsilon = 0.000001f;

        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .SetTypicalOpen(GameRelease.SkyrimSE, "WeaponSpeedEffectsFix.esp")
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .Run(args);
        }

        private static ISet<FormKey> inScopeEffects = new HashSet<FormKey>();
        private static IPatcherState<ISkyrimMod, ISkyrimModGetter>? patcherState;

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            patcherState = state;
            // build a list of relevant MGEF records: anything that affects Weapon Speed in either hand
            foreach (var effect in state.LoadOrder.PriorityOrder.WinningOverrides<IMagicEffectGetter>())
            {
                ActorValue firstAV = effect.Archetype.ActorValue;
                ActorValue secondAV = effect.SecondActorValue;
                if (firstAV != ActorValue.WeaponSpeedMult && firstAV != ActorValue.LeftWeaponSpeedMultiply &&
                    secondAV != ActorValue.WeaponSpeedMult && secondAV != ActorValue.LeftWeaponSpeedMultiply)
                    continue;

                inScopeEffects.Add(effect.FormKey);

                // add script if WSEF is present - this includes every in-scope MGEF, so applicable values are reset
                // during OnEffectStart
                if (WeaponEffectsSpeedModKey is not null)
                {
                    var effectMod = state.LoadOrder[state.LoadOrder.IndexOf(effect.FormKey.ModKey)];
                    if (!effectMod.Mod!.MasterReferences.Any(reference => reference.Master == WeaponEffectsSpeedModKey))
                    {
                        var vmad = effect.VirtualMachineAdapter;
                        if (vmad is null || !vmad.Scripts.Any(script => script.Name == WSEFScript))
                        {
                            var newEffect = state.PatchMod.MagicEffects.GetOrAddAsOverride(effect);
                            if (vmad is null)
                                newEffect.VirtualMachineAdapter = new VirtualMachineAdapter();
                            newEffect.VirtualMachineAdapter!.Scripts.Add(new ScriptEntry
                            {
                                Name = WSEFScript
                            });
                        }
                    }
                }
            }

            // For each SPEL/SCRL/ENCH process any linked relevant MGEFs
            ProcessItemEffects<ISpellGetter>();
            ProcessItemEffects<IScrollGetter>();
            ProcessItemEffects<IObjectEffectGetter>();
        }

        private static void ProcessItemEffects<T>() where T : class, IMajorRecordGetter
        {
            foreach (var item in patcherState!.LoadOrder.PriorityOrder.WinningOverrides<T>())
            {
                // skip records with no reference to any in-scope MGEF
                if (!item!.ContainedFormLinks.Any(link => inScopeEffects.Contains(link.FormKey)))
                    continue;

                // skip records where mod has WSEF or ASF as a master
                var itemMod = patcherState.LoadOrder[patcherState.LoadOrder.IndexOf(item.FormKey.ModKey)];
                if (itemMod.Mod!.MasterReferences.Any(
                    reference => reference.Master == WeaponEffectsSpeedModKey || reference.Master == AttackSpeedFrameworkModKey))
                    continue;

                // process Effects on the item in hand
                ProcessEffects(item);
            }
        }

        private static void ProcessEffects<T>(T? item) where T : class, IMajorRecordGetter
        {
            if (item is null)
                return;
            PropertyInfo? info = item.GetType().GetProperty("Effects", typeof(IReadOnlyList<IEffectGetter>));
            if (info is null)
                return;
            if (info.GetValue(item) is not IReadOnlyList<IEffectGetter> effects)
                return;
            int index = -1;
            foreach (var effect in effects)
            {
                ++index;
                if (!inScopeEffects.Contains(effect.BaseEffect.FormKey) || effect.Data is null)
                    continue;

                float mag = effect.Data!.Magnitude;
                float newMag = mag - 1.0f;
                if (newMag < -FloatEpsilon || mag > IgnoreMagnitudesStartingFrom)
                    continue;
                if ((Math.Abs(newMag) < FloatEpsilon) && IgnoreMagnitude_1_0)
                    continue;
                // switch on valid inputs
                if (item is ISpellGetter spell)
                {
                    var newSpell = patcherState!.PatchMod.Spells.GetOrAddAsOverride(spell);
                    newSpell.Effects[index].Data!.Magnitude -= 1.0f;
                }
                else if (item is IScrollGetter scroll)
                {
                    var newScroll = patcherState!.PatchMod.Scrolls.GetOrAddAsOverride(scroll);
                    newScroll.Effects[index].Data!.Magnitude -= 1.0f;
                }
                else if (item is IObjectEffectGetter objectEffect)
                {
                    var newObjectEffect = patcherState!.PatchMod.ObjectEffects.GetOrAddAsOverride(objectEffect);
                    newObjectEffect.Effects[index].Data!.Magnitude -= 1.0f;
                }
            }
        }

    }
}
