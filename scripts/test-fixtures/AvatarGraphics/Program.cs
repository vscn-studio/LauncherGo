using System.Reflection;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ServerMap.Render;
using ServerMap.Client;
using Vintagestory.API.Client;

if (args.Length != 2) throw new ArgumentException("GameRoot and output PNG path required");
var game = Path.GetFullPath(args[0]);
AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
{
    var name = new AssemblyName(request.Name).Name + ".dll";
    foreach (var folder in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") })
    { var file = Path.Combine(folder, name); if (File.Exists(file)) return Assembly.LoadFrom(file); }
    return null;
};
Environment.SetEnvironmentVariable("PATH", Path.Combine(game, "Lib") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
using var window = new NativeWindow(new NativeWindowSettings { StartVisible = false, ClientSize = new Vector2i(64, 64), API = ContextAPI.OpenGL, APIVersion = new Version(3, 3) });
window.Context.MakeCurrent(); GL.LoadBindings(new GLFWBindingsContext());
var texture = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, texture);
byte[] pixels = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 0, 255];
GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 2, 2, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
GL.PixelStore(PixelStoreParameter.PackAlignment, 8); GL.PixelStore(PixelStoreParameter.PackRowLength, 7);
var mod = Assembly.Load("ServerMap");
var equipmentTexture = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, equipmentTexture);
byte[] equipmentPixels = [90, 160, 240, 255];
GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, equipmentPixels);
// Native callback meshes contain joint IDs; retain head + child hair/hat joints,
// without altering the full mesh used by the actual character renderer.
var bodyMesh = new MeshData(12, 18) { CustomInts = new CustomMeshDataPartInt { InterleaveSizes = [1], InterleaveOffsets = [0], InterleaveStride = 0 } };
for (var face = 0; face < 3; face++)
{
    var first = bodyMesh.VerticesCount;
    bodyMesh.AddVertex(0, 0, face, 0, 0); bodyMesh.AddVertex(0, 0, face + 1, 1, 0);
    bodyMesh.AddVertex(0, 1, face + 1, 1, 1); bodyMesh.AddVertex(0, 1, face, 0, 1);
    bodyMesh.AddIndices(first, first+1, first+2, first, first+2, first+3);
    bodyMesh.AddTextureId(face == 2 ? equipmentTexture : texture);
    for (var v = 0; v < 4; v++) bodyMesh.CustomInts.Add(face);
}
var headMesh = ClientHeadCapture.ExtractHeadMesh(bodyMesh, new HashSet<int> { 1, 2 });
if (headMesh.VerticesCount != 8 || headMesh.IndicesCount != 12 || bodyMesh.VerticesCount != 12 ||
    !headMesh.TextureIds.Contains(equipmentTexture) || headMesh.CustomInts.Values.Take(headMesh.CustomInts.Count).Contains(0))
    throw new Exception("Head/hair/equipment extraction changed the native mesh or lost equipment texture IDs");
var captured = ClientHeadCapture.CaptureMesh(headMesh, [new LoadedTexture(null, texture, 2, 2), new LoadedTexture(null, equipmentTexture, 1, 1)]);
if (captured.Vertices.Length != 12 || captured.Textures.Length != 1 || !captured.Textures[0].Rgba.Chunk(4).Any(p=>p.SequenceEqual(equipmentPixels)))
    throw new Exception("Native mesh capture lost headgear or cross-atlas colors");
var nativePng = AvatarScene.Unpack(captured.Pack()).Render();
if (nativePng.Length < 100) throw new Exception("Native head pipeline produced an empty avatar");
if (ClientHiddenMap.MaskDepth <= 60 || ClientHiddenMap.MaskDepth >= 150) throw new Exception("Mask depth overlaps the next dialog slice");
using var captureHooks = (IDisposable)mod.GetType("ServerMap.Client.ClientHeadCapture")!.GetMethod("Start")!.Invoke(null, [null])!;
using var mapHooks = (IDisposable)Activator.CreateInstance(mod.GetType("ServerMap.Client.ClientHiddenMap")!, new object?[] { null })!;
var capture = mod.GetType("ServerMap.Client.ClientHeadCapture")!.GetNestedType("TextureReadback", BindingFlags.NonPublic)!.GetMethod("Read")!;
var actual = (byte[])capture.Invoke(null, [texture, 1, 0, 1, 2])!;
if (!actual.SequenceEqual(new byte[] { 0, 255, 0, 255, 255, 255, 0, 255 })) throw new Exception("Atlas cropping/orientation mismatch");
if (GL.GetInteger(GetPName.PackAlignment) != 8 || GL.GetInteger(GetPName.PackRowLength) != 7 || GL.GetInteger(GetPName.ReadFramebufferBinding) != 0) throw new Exception("GL state was not restored");
var scene = new AvatarScene { Textures = [new(2, 2, pixels)], Vertices = [new(0, -1, -1, 0, 0, 0), new(0, -1, 1, 1, 0, 0), new(0, 1, 1, 1, 1, 0), new(0, -1, -1, 0, 0, 0), new(0, 1, 1, 1, 1, 0), new(0, 1, -1, 0, 1, 0)] };
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!); File.WriteAllBytes(args[1], scene.Render());
GL.DeleteTexture(texture);
GL.DeleteTexture(equipmentTexture);
Console.WriteLine("PASS native head/hair/equipment mesh extraction, cross-atlas head capture, dialog mask depth, game hooks, GL crop/state restore and server PNG rasterization");
