using HarmonyLib;
using ServerMap.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace ServerMap.Client;

/// <summary>Reversible client display mask, applies equally to the minimap and full map.</summary>
public sealed class ClientHiddenMap : IDisposable
{
    // Map terrain is at 50, waypoints at 60; stay inside this dialog's
    // 150-unit depth slice so later dialogs (notably Add Waypoint) stay on top.
    public const float MaskDepth = 70;
    private static ClientHiddenMap? current;
    private readonly ICoreClientAPI api;
    private readonly Harmony harmony = new("servermap-ingame-hidden-regions");
    private LoadedTexture? texture;
    private double[] bounds = [];
    public ClientHiddenMap(ICoreClientAPI api)
    {
        this.api = api; current = this;
        harmony.Patch(AccessTools.Method(typeof(GuiElementMap), nameof(GuiElementMap.RenderInteractiveElements)), postfix: new HarmonyMethod(typeof(ClientHiddenMap), nameof(AfterRender)));
        harmony.Patch(AccessTools.Method(typeof(GuiDialogWorldMap), nameof(GuiDialogWorldMap.OnMouseMove)), postfix: new HarmonyMethod(typeof(ClientHiddenMap), nameof(AfterMouseMove)));
        harmony.Patch(AccessTools.Method(typeof(GuiDialogWorldMap), nameof(GuiDialogWorldMap.OnMouseUp)), prefix: new HarmonyMethod(typeof(ClientHiddenMap), nameof(BeforeMouseUp)));
    }
    public void Apply(ServerHiddenMapPacket packet)
    {
        var values = packet.Bounds;
        if (values == null || values.Length > 256 * 4 || values.Length % 4 != 0 || values.Any(v => !double.IsFinite(v) || Math.Abs(v) > 32_000_000)) return;
        for (var i = 0; i < values.Length; i += 4) if (values[i + 2] <= values[i] || values[i + 3] <= values[i + 1]) return;
        bounds = values.ToArray();
    }
    public void Clear() => bounds = [];
    private static void AfterRender(GuiElementMap __instance) => current?.Draw(__instance);
    private bool HiddenAt(GuiElementMap map, MouseEvent mouse)
    {
        var px = (mouse.X - map.Bounds.renderX) / map.Bounds.InnerWidth; var py = (mouse.Y - map.Bounds.renderY) / map.Bounds.InnerHeight;
        if (px < 0 || px > 1 || py < 0 || py > 1) return false;
        var view = map.CurrentBlockViewBounds; var x = view.X1 + px * (view.X2 - view.X1); var z = view.Z1 + py * (view.Z2 - view.Z1);
        for (var i = 0; i < bounds.Length; i += 4) if (x >= bounds[i] && x <= bounds[i + 2] && z >= bounds[i + 1] && z <= bounds[i + 3]) return true;
        return false;
    }
    private static void AfterMouseMove(GuiDialogWorldMap __instance, MouseEvent args)
    {
        if (__instance.SingleComposer?.GetElement("mapElem") is GuiElementMap map && current?.HiddenAt(map, args) == true)
            __instance.SingleComposer.GetHoverText("hoverText")?.SetNewText("");
    }
    private static bool BeforeMouseUp(GuiDialogWorldMap __instance, MouseEvent args)
    {
        if (__instance.SingleComposer?.GetElement("mapElem") is not GuiElementMap map || current?.HiddenAt(map, args) != true) return true;
        map.IsDragingMap = false; args.Handled = true; return false;
    }
    private void Draw(GuiElementMap map)
    {
        if (bounds.Length == 0) return;
        var view = map.CurrentBlockViewBounds; if (view.X2 <= view.X1 || view.Z2 <= view.Z1) return;
        if (texture == null)
        {
            texture = new LoadedTexture(api, 0, 1, 1);
            api.Render.LoadOrUpdateTextureFromRgba([unchecked((int)0xff292623)], false, 0, ref texture);
        }
        api.Render.PushScissor(map.Bounds);
        try
        {
            for (var i = 0; i < bounds.Length; i += 4)
            {
                var x0 = Math.Max(view.X1, bounds[i]); var z0 = Math.Max(view.Z1, bounds[i + 1]);
                var x1 = Math.Min(view.X2, bounds[i + 2]); var z1 = Math.Min(view.Z2, bounds[i + 3]);
                if (x0 >= x1 || z0 >= z1) continue;
                var left = map.Bounds.renderX + (x0 - view.X1) / (view.X2 - view.X1) * map.Bounds.InnerWidth;
                var top = map.Bounds.renderY + (z0 - view.Z1) / (view.Z2 - view.Z1) * map.Bounds.InnerHeight;
                var width = (x1 - x0) / (view.X2 - view.X1) * map.Bounds.InnerWidth;
                var height = (z1 - z0) / (view.Z2 - view.Z1) * map.Bounds.InnerHeight;
                api.Render.Render2DTexture(texture.TextureId, (float)left, (float)top, (float)width, (float)height, MaskDepth);
            }
        }
        finally { api.Render.PopScissor(); }
    }
    public void Dispose() { if (current == this) current = null; harmony.UnpatchAll(harmony.Id); texture?.Dispose(); bounds = []; }
}
