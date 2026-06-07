using UnityEditor;

/// <summary>
/// Import settings for the PlanD photo-realistic tiles (Assets/HongKongPhoto/*):
/// the OBJs ship without vertex normals (vn=0) so they must be calculated, and the
/// meshes stay readable for tri counting + collider cooking. Texture atlases are
/// 8192x4096 at source (6 cm GSD); MaxTex picks the import ceiling — 8192 is ship
/// quality (M5 build), 4096 is the dev-box iteration sweet spot.
/// </summary>
public class HKPhotoImportSettings : AssetPostprocessor
{
    const string Root = "Assets/HongKongPhoto/";
    // 8192 = ship quality: original atlas res baked into the Mac/Windows builds.
    // Drop to 4096 for iteration (faster cycles, ~640 MB less GPU memory on the dev iGPU);
    // after flipping, run DroneSim/HK/P Retex Photo Tiles to reimport existing atlases.
    public const int MaxTex = 8192;

    void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(Root)) return;
        var imp = (ModelImporter)assetImporter;
        imp.importNormals = ModelImporterNormals.Calculate;
        imp.normalSmoothingAngle = 60f;
        imp.isReadable = true;
        imp.materialLocation = ModelImporterMaterialLocation.InPrefab;
    }

    void OnPreprocessTexture()
    {
        // surround package: big satellite terrain texture needs headroom (covers ~15 km)
        if (assetPath.StartsWith("Assets/HongKongSurround/"))
        {
            var simp = (TextureImporter)assetImporter;
            simp.maxTextureSize = 8192;
            simp.mipmapEnabled = true;
            simp.streamingMipmaps = true;
            return;
        }
        if (!assetPath.StartsWith(Root)) return;
        var imp = (TextureImporter)assetImporter;
        imp.maxTextureSize = MaxTex;
        imp.mipmapEnabled = true;
        imp.streamingMipmaps = true;
        imp.isReadable = true; // TrimWater samples atlas colors per triangle
    }
}
