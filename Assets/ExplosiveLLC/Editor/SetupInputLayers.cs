using UnityEditor;
using UnityEngine;

namespace RPGCharacterAnims
{
	class SetupInputLayers:AssetPostprocessor
	{
		// O projeto RollBack não usa os inputs/layers do pack (tem controller próprio).
		// Antes o aviso abria a cada importação de asset; agora aparece só uma vez por projeto.
		static string ShownKey => "RPGCharacterAnims.SetupMessageShown." + Application.dataPath;

		static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
		{
			if (Application.isBatchMode || EditorPrefs.GetBool(ShownKey, false))
				return;
			EditorPrefs.SetBool(ShownKey, true);

			SetupMessageWindow window = ( SetupMessageWindow )EditorWindow.GetWindow(typeof(SetupMessageWindow), true, "Load Input and Layer Presets");
			window.maxSize = new Vector2(290f, 190f);
			window.minSize = window.maxSize;
		}
	}

	public class SetupMessageWindow:EditorWindow
	{
		void OnGUI()
		{
			EditorGUILayout.LabelField("Before attempting to use the RPG Character Animation Mecanim Animation Pack FREE, you must first ensure that the INPUTS and LAYERS are correctly defined.  There is an included Input.preset and TagLayer.preset which contains all the settings that you can load in.\n \nThe README and other documents are located in the Documentation folder.\n \nYou can remove this message by deleting the SetupInputLayers.cs script in the Editor folder.", EditorStyles.wordWrappedLabel);
		}
	}
}