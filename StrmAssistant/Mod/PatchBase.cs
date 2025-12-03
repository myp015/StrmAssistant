using System;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public abstract class PatchBase<T> where T : PatchBase<T>
    {
        public PatchTracker PatchTracker;

        public static T Instance { get; private set; }

        protected PatchBase()
        {
            Instance = (T)this;
            PatchTracker = new PatchTracker(typeof(T), PatchApproach.Harmony);
        }

        protected void Initialize()
        {
            PatchTracker.Status = PatchStatus.Initializing;
            
            try
            {
                OnInitialize();
                PatchTracker.Status = PatchStatus.Initialized;
                PatchTracker.InitializedAt = DateTime.Now;
            }
            catch (Exception e)
            {
                PatchTracker.Status = PatchStatus.Failed;
                PatchTracker.AddError($"Initialization failed: {e.Message}");
                
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(e.Message);
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }

                Plugin.Instance.Logger.Warn($"{PatchTracker.PatchType.Name} Init Failed: {e.Message}");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
            }

            if (PatchTracker.FallbackPatchApproach == PatchApproach.None)
            {
                PatchTracker.Status = PatchStatus.NotSupported;
                return;
            }

            if (HarmonyMod is null)
            {
                PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
                Plugin.Instance.Logger.Debug($"{PatchTracker.PatchType.Name} using Reflection (Harmony unavailable)");
            }
        }

        protected abstract void OnInitialize();

        protected abstract void Prepare(bool apply);

        public void Patch() => Prepare(true);

        public void Unpatch() => Prepare(false);
    }
}
