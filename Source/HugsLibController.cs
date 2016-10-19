﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HugsLib.Settings;
using RimWorld;
using UnityEngine;
using Verse;

namespace HugsLib {
	[StaticConstructorOnStartup]
	/**
	 * The hub of the library. Instantiates classes that extend ModBase and forwards some of the most useful events to them.
	 * The library is designed to be updated safely, so that you can have multiple versions of it running at the same time.
	 * When a mod implements functionality from this library, it is locked to that version and must bundle the dll with its own assembly.
	 * Ideally, all mods using the library will be using the same version, but should that not be the case, a warning will be issued
	 * and all present versions will be able to continue running in parallel.
	 * However, only mods implementing the latest version will be able to have their settings changed in the Mod Settings screen.
	 * Note: The $ in the assembly name is necessary for proper assembly load order
	 */
	public class HugsLibController {
		private const string SceneObjectNameBase = "HugsLibProxy";
		private const string ModIdentifier = "HugsLib";
		private const int MapLevelIndex = 1;

		private static HugsLibController instance;
		public static HugsLibController Instance {
			get { return instance ?? (instance = new HugsLibController()); }
		}

		public static AssemblyName AssemblyName {
			get { return typeof(HugsLibController).Assembly.GetName(); }
		}

		public static VersionShort AssemblyVersion {
			get { return AssemblyName.Version; }
		}

		public static string SceneObjectName {
			get {
				return string.Concat(SceneObjectNameBase, "-", AssemblyVersion);
			}
		}

		// entry point
		static HugsLibController() {
			Logger = new ModLogger(ModIdentifier);
			CreateSceneObject();
		}

		private static bool VersionConflict;
		private static bool TopVersionInConflict;

		internal static ModLogger Logger { get; private set; }

		private static void CreateSceneObject() {
			var obj = new GameObject(SceneObjectName);
			GameObject.DontDestroyOnLoad(obj);
			// black magic, stand clear. See UnityProxyComponent docs for details
			try {
				var assemblyName = new AssemblyName {Name = "HugsLibAdHocAssembly"};
				var assemblyBuilder = Thread.GetDomain().DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
				var module = assemblyBuilder.DefineDynamicModule("adHocModule");
				var typeBuilder = module.DefineType("ProxyComponent" + Math.Abs(AssemblyName.GetHashCode()), TypeAttributes.Public | TypeAttributes.Class, typeof (UnityProxyComponent));
				var generatedType = typeBuilder.CreateType();
				obj.AddComponent(generatedType);
			} catch (Exception e) {
				Logger.Warning("Failed to create dynamic type for component, using fallback instead. Exception was: "+e);
				obj.AddComponent(typeof (UnityProxyComponent));
			}
			// end of black magic
		}
		
		private readonly List<ModBase> childMods = new List<ModBase>();
		private readonly List<ModBase> initializedMods = new List<ModBase>(); 
		private Dictionary<Assembly, ModContentPack> assemblyContentPacks;
		private DefReloadWatcher reloadWatcher;
		private WindowReplacer<Dialog_Options, Dialog_OptionsExtended> optionsReplacer;
		private LanguageStringInjector languageInjector;
		private SettingHandle<bool> updateNewsHandle;
		private bool mapLoadedPending = true;

		public ModSettingsManager Settings { get; private set; }
		public UpdateFeatureManager UpdateFeatures { get; private set; }
		public CallbackScheduler CallbackScheduler { get; private set; } // initalized before MapLoaded
		public DistributedTickScheduler DistributedTicker { get; private set; }  // initalized before MapLoaded
		
		private HugsLibController() {
		}

		internal void Initalize() {
			if (Settings != null) return; // double initialization safeguard, shouldn't happen
			try {
				Settings = new ModSettingsManager(OnSettingsChanged);
				RegisterOwnSettings();
				UpdateFeatures = new UpdateFeatureManager();
				CallbackScheduler = new CallbackScheduler();
				DistributedTicker = new DistributedTickScheduler();
				reloadWatcher = new DefReloadWatcher(OnDefReloadDetected, typeof(HugsLibController).Assembly.GetHashCode());
				optionsReplacer = new WindowReplacer<Dialog_Options, Dialog_OptionsExtended>();
				languageInjector = new LanguageStringInjector();
				LoadReloadInitialize();
				RegisterOwnSettings();
			} catch (Exception e) {
				Logger.ReportException("Initalize", e);
			}
		}

		internal void OnUpdate() {
			string modId = null;
			try {
				reloadWatcher.Update();
				for (int i = 0; i < childMods.Count; i++) {
					modId = childMods[i].ModIdentifier;
					childMods[i].Update();
				}
			} catch (Exception e) {
				Logger.ReportException("OnUpdate", e, modId, true);
			}
		}

		public void OnTick() {
			string modId = null;
			try {
				var currentTick = Find.TickManager.TicksGame;
				for (int i = 0; i < childMods.Count; i++) {
					modId = childMods[i].ModIdentifier;
					childMods[i].Tick(currentTick);
				}
				modId = null;
				CallbackScheduler.Tick(currentTick);
				DistributedTicker.Tick(currentTick);
			} catch (Exception e) {
				Logger.ReportException("OnTick", e, modId, true);
			}
		}

		internal void OnFixedUpdate() {
			if (mapLoadedPending && Current.ProgramState == ProgramState.MapPlaying) {
				mapLoadedPending = false;
				// Make sure we execute after MapDrawer.RegenerateEverythingNow
				LongEventHandler.ExecuteWhenFinished(OnMapLoaded);
			}
			string modId = null;
			try {
				for (int i = 0; i < childMods.Count; i++) {
					modId = childMods[i].ModIdentifier;
					childMods[i].FixedUpdate();
				}
			} catch (Exception e) {
				Logger.ReportException("OnFixedUpdate", e, modId, true);
			}
		}

		internal void OnGUI() {
			string modId = null;
			try {
				if (!VersionConflict || TopVersionInConflict) {
					optionsReplacer.OnGUI();
				}
				for (int i = 0; i < childMods.Count; i++) {
					modId = childMods[i].ModIdentifier;
					childMods[i].OnGUI();
				}
			} catch (Exception e) {
				Logger.ReportException("OnGUI", e, modId, true);
			}
		}

		internal void OnLevelLoaded(int level) {
			string modId = null;
			try {
				for (int i = 0; i < childMods.Count; i++) {
					modId = childMods[i].ModIdentifier;
					childMods[i].LevelLoaded(level);
				}
				if (level != MapLevelIndex) return;
				for (int i = 0; i < childMods.Count; i++) {
					modId = childMods[i].ModIdentifier;
					childMods[i].MapLoading();
				}
			} catch (Exception e) {
				Logger.ReportException("OnLevelLoaded", e, modId);
			}
		}

		internal void OnMapComponentsIntializing() {
			mapLoadedPending = true;
			string modId = ModIdentifier;
			try {
				var currentTick = Find.TickManager.TicksGame;
				CallbackScheduler.Initialize(currentTick);
				DistributedTicker.Initialize(currentTick);
				Current.Game.tickManager.RegisterAllTickabilityFor(new HugsTickProxy{CreatedByController = true});
				for (int i = 0; i < childMods.Count; i++) {
					modId = childMods[i].ModIdentifier;
					childMods[i].MapComponentsInitializing();
				}
			} catch (Exception e) {
				Logger.ReportException("OnMapComponentsIntializing", e, modId);
			}
		}

		private void OnMapLoaded(){
			string modId = ModIdentifier;
			try {
				for (int i = 0; i < childMods.Count; i++) {
					modId = childMods[i].ModIdentifier;
					childMods[i].MapLoaded();
				}
				modId = null;
				// show update news dialog
				if ((!VersionConflict || TopVersionInConflict) && updateNewsHandle.Value) {
					UpdateFeatures.TryShowDialog();
				}
			} catch (Exception e) {
				Logger.ReportException("OnMapLoaded", e, modId);
			}
		}

		private void OnSettingsChanged() {
			string modId = null;
			try {
				for (int i = 0; i < childMods.Count; i++) {
					modId = childMods[i].ModIdentifier;
					childMods[i].SettingsChanged();
				}
			} catch (Exception e) {
				Logger.ReportException("OnSettingsChanged", e, modId);
			}
		}

		private void OnDefsLoaded() {
			string modId = null;
			try {
				for (int i = 0; i < childMods.Count; i++) {
					modId = childMods[i].ModIdentifier;
					childMods[i].DefsLoaded();
				}
			} catch (Exception e) {
				Logger.ReportException("OnDefsLoaded", e, modId);
			}
		}

		private void OnDefReloadDetected() {
			LoadReloadInitialize();
		}
		
		// executed both at startup and after a def reload
		private void LoadReloadInitialize() {
			string modId = null;
			try {
				DetectVersionConflict();
				EnumerateModAssemblies();
				EnumerateChildMods();
				languageInjector.InjectEmbeddedStrings();
				var initializationsThisRun = new List<string>();
				for (int i = 0; i < childMods.Count; i++) {
					var childMod = childMods[i];
					childMod.ModIsActive = assemblyContentPacks.ContainsKey(childMod.GetType().Assembly);
					if(initializedMods.Contains(childMod)) continue; // no need to reinitialize already loaded mods
					initializedMods.Add(childMod);
					modId = childMod.ModIdentifier;
					childMod.Initalize();
					initializationsThisRun.Add(modId);
				}
				if (initializationsThisRun.Count > 0) {
					Logger.TraceFormat("v{0} initialized {1}", AssemblyVersion, initializationsThisRun.ListElements());
				}
				OnDefsLoaded();
			} catch (Exception e) {
				Logger.ReportException("LoadReloadInitialize", e, modId);
			}
		}
		
		// will run on startup and on reload. On reload it will add newly loaded mods
		private void EnumerateChildMods() {
			foreach (var subclass in typeof (ModBase).InstantiableDescendantsAndSelf()) {
				if (childMods.Find(cm => cm.GetType() == subclass) != null) continue; // skip duplicate types present in multiple assemblies
				ModContentPack pack;
				assemblyContentPacks.TryGetValue(subclass.Assembly, out pack);
				if (pack == null) continue; // mod is disabled
				ModBase modbase = null;
				try {
					modbase = (ModBase) Activator.CreateInstance(subclass, true);
					modbase.ModContentPack = pack;
					if (childMods.Find(m => m.ModIdentifier == modbase.ModIdentifier) != null) {
						Logger.Error("Duplicate mod identifier: " + modbase.ModIdentifier);
						continue;
					}
					childMods.Add(modbase);
				} catch (Exception e) {
					Logger.ReportException("child mod instantiation", e, subclass.ToString());
				}
				if (modbase != null) UpdateFeatures.InspectActiveMod(modbase.ModIdentifier, subclass.Assembly.GetName().Version);
			}
			// sort by load order
			childMods.Sort((cm1, cm2) => cm1.ModContentPack.loadOrder.CompareTo(cm2.ModContentPack.loadOrder));
		}

		private void EnumerateModAssemblies() {
			assemblyContentPacks = new Dictionary<Assembly, ModContentPack>();
			foreach (var modContentPack in LoadedModManager.RunningMods) {
				foreach (var loadedAssembly in modContentPack.assemblies.loadedAssemblies) {
					assemblyContentPacks[loadedAssembly] = modContentPack;
				}
			}
		}

		// check if library is included in other mods, warn on different versions used
		private void DetectVersionConflict() {
			var ownName = AssemblyName.Name;
			var ownVersion = AssemblyName.Version;
			TopVersionInConflict = true;
			List<string> conflictingMods = null;
			// check assemblies in all mods
			foreach (var modContentPack in LoadedModManager.RunningMods) {
				if(!modContentPack.LoadedAnyAssembly) continue;
				foreach (var loadedAssembly in modContentPack.assemblies.loadedAssemblies) {
					var otherAssemblyName = loadedAssembly.GetName();
					if(otherAssemblyName.Name != ownName) continue;
					if(otherAssemblyName.Version == ownVersion) continue;
					VersionConflict = true;
					if (otherAssemblyName.Version > ownVersion) {
						TopVersionInConflict = false;
					}
					if(conflictingMods == null) conflictingMods = new List<string>();
					conflictingMods.Add(modContentPack.Name);
					break;
				}
			}
			if(conflictingMods == null || !TopVersionInConflict) return;
			Logger.Warning("Multiple versions of a shared library are in use. Please update the following mods: "+conflictingMods.ListElements());
		}

		private void RegisterOwnSettings() {
			var pack = Settings.GetModSettings(ModIdentifier);
			pack.EntryName = "HugsLib_ownSettingsName".Translate();
			pack.DisplayPriority = ModSettingsPack.ListPriority.Lower;
			updateNewsHandle = pack.GetHandle("modUpdateNews", "HugsLib_setting_showNews_label".Translate(), "HugsLib_setting_showNews_desc".Translate(), true);
			var allNewsHandle = pack.GetHandle("showAllNews", "HugsLib_setting_allNews_label".Translate(), "HugsLib_setting_allNews_desc".Translate(), false);
			allNewsHandle.Unsaved = true;
			allNewsHandle.CustomDrawer = rect => {
				if (Widgets.ButtonText(rect, "HugsLib_setting_allNews_button".Translate())) {
					if (!UpdateFeatures.TryShowDialog(true)) {
						Find.WindowStack.Add(new Dialog_Message("HugsLib_setting_allNews_fail".Translate()));
					}
				}
				return false;
			};
		}
	}
}