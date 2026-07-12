using System.Globalization;
using System.Text.RegularExpressions;

namespace PCBHelper.Core;

public sealed class BoardFinishingService
{
    private readonly ProjectDiscoveryService _projects;
    public BoardFinishingService(ProjectDiscoveryService projects) => _projects = projects;

    public ToolResponse<BoardFinishingMutationResult> AddCopperZone(string projectPath, string net, string layer, string points, double clearance, double minThickness, bool dryRun)
    {
        var loaded = Load(projectPath); if (!loaded.Success || loaded.Data is null) return Fail(loaded);
        if (layer is not ("F.Cu" or "B.Cu")) return Error("Only F.Cu and B.Cu zones are supported.", "UNSUPPORTED_LAYER");
        var polygon = ParsePoints(points); if (polygon is null || polygon.Count < 3 || clearance < 0 || minThickness <= 0) return Error("A zone requires at least three valid points and positive geometry.", "INVALID_ZONE_GEOMETRY");
        var board = loaded.Data.Board; var resolved = board.Nets.FirstOrDefault(item => item.Name.Equals(net, StringComparison.OrdinalIgnoreCase) || item.Code.ToString(CultureInfo.InvariantCulture) == net);
        if (resolved is null) return Error($"Net not found: {net}", "NET_NOT_FOUND");
        var uuid = Guid.NewGuid().ToString();
        var text = $"\n\t(zone\n\t\t(net {resolved.Code})\n\t\t(net_name \"{Escape(resolved.Name)}\")\n\t\t(layer \"{layer}\")\n\t\t(uuid \"{uuid}\")\n\t\t(hatch edge 0.5)\n\t\t(connect_pads (clearance {F(clearance)}))\n\t\t(min_thickness {F(minThickness)})\n\t\t(fill yes (thermal_gap 0.3) (thermal_bridge_width 0.3))\n\t\t(polygon (pts {string.Join(' ', polygon.Select(p => $"(xy {F(p.X)} {F(p.Y)})"))}))\n\t)";
        return Insert(loaded.Data, "add-copper-zone", uuid, text, dryRun);
    }

    public ToolResponse<BoardFinishingMutationResult> UpdateCopperZone(string projectPath, string zone, string? net, string? layer, string? points, bool dryRun)
    {
        var loaded = Load(projectPath); if (!loaded.Success || loaded.Data is null) return Fail(loaded);
        var block = FindBlock(loaded.Data.Text, "zone", zone); if (block is null) return Error($"Zone not found: {zone}", "ZONE_NOT_FOUND");
        var updated = block.Value.Text;
        if (layer is not null) { if (layer is not ("F.Cu" or "B.Cu")) return Error("Only F.Cu and B.Cu zones are supported.", "UNSUPPORTED_LAYER"); updated = ReplaceFirst(updated, """\(layer\s+"[^"]+"\)""", $"(layer \"{layer}\")"); }
        if (net is not null)
        {
            var resolved = loaded.Data.Board.Nets.FirstOrDefault(item => item.Name.Equals(net, StringComparison.OrdinalIgnoreCase) || item.Code.ToString(CultureInfo.InvariantCulture) == net);
            if (resolved is null) return Error($"Net not found: {net}", "NET_NOT_FOUND");
            updated = ReplaceFirst(updated, @"\(net\s+\d+\)", $"(net {resolved.Code})");
            updated = ReplaceFirst(updated, """\(net_name\s+"[^"]*"\)""", $"(net_name \"{Escape(resolved.Name)}\")");
        }
        if (points is not null)
        {
            var polygon = ParsePoints(points); if (polygon is null || polygon.Count < 3) return Error("A zone requires at least three valid points.", "INVALID_ZONE_GEOMETRY");
            var polygonStart=updated.IndexOf("(polygon",StringComparison.Ordinal);var polygonEnd=polygonStart<0?-1:FindEnd(updated,polygonStart);if(polygonEnd<0)return Error("Zone polygon is invalid.","INVALID_ZONE_GEOMETRY");
            var polygonText=$"(polygon (pts {string.Join(' ', polygon.Select(p => $"(xy {F(p.X)} {F(p.Y)})"))}))";updated=updated.Remove(polygonStart,polygonEnd-polygonStart+1).Insert(polygonStart,polygonText);
        }
        return Replace(loaded.Data, "update-copper-zone", zone, block.Value.Start, block.Value.Length, updated, dryRun);
    }

    public ToolResponse<BoardFinishingMutationResult> MoveReferenceText(string projectPath, string reference, double x, double y, bool dryRun) => EditReference(projectPath, reference, dryRun, block =>
        ReplaceFirst(block, """(\(property\s+"Reference"\s+"[^"]+"[\s\S]*?\(at\s+)-?[\d.]+\s+-?[\d.]+""", $"$1{F(x)} {F(y)}"), "move-reference-text");

    public ToolResponse<BoardFinishingMutationResult> HideReferenceText(string projectPath, string reference, bool dryRun) => EditReference(projectPath, reference, dryRun, block =>
        Regex.IsMatch(block, """\(property\s+"Reference"[\s\S]*?\(hide\s+yes\)""") ? block : ReplaceFirst(block, """(\(property\s+"Reference"[\s\S]*?\(layer\s+"[^"]+"\))""", "$1 (hide yes)"), "hide-reference-text");

    public ToolResponse<BoardFinishingMutationResult> CleanupSilkscreen(string projectPath, double minimumSpacing, bool dryRun)
    {
        var loaded=Load(projectPath);if(!loaded.Success||loaded.Data is null)return Fail(loaded);if(minimumSpacing<=0)return Error("Minimum spacing must be positive.","INVALID_SILKSCREEN_GEOMETRY");
        var refs=loaded.Data.Board.Footprints.Where(f=>f.Reference is not null&&f.XMillimeters is not null&&f.YMillimeters is not null&&f.Properties.TryGetValue("Reference",out _))
            .Select(f=>new{Footprint=f,Property=f.Properties["Reference"],X=f.XMillimeters!.Value,Y=f.YMillimeters!.Value}).ToArray();
        var hide=new HashSet<KiCadFootprint>();
        for(var i=0;i<refs.Length;i++)for(var j=i+1;j<refs.Length;j++)if(Math.Sqrt(Math.Pow(refs[i].X-refs[j].X,2)+Math.Pow(refs[i].Y-refs[j].Y,2))<minimumSpacing)hide.Add(refs[j].Footprint);
        if(hide.Count==0)return ToolResponse<BoardFinishingMutationResult>.Ok("Silkscreen cleanup found no overlapping reference anchors.",new("cleanup-silkscreen","none",loaded.Data.File,true,string.Empty));
        var text=loaded.Data.Text;foreach(var fp in hide.OrderByDescending(f=>f.SourceStart)){var block=text.Substring(fp.SourceStart,fp.SourceLength);var edited=Regex.IsMatch(block,"""\(property\s+"Reference"[\s\S]*?\(hide\s+yes\)""")?block:ReplaceFirst(block,"""(\(property\s+"Reference"[\s\S]*?\(layer\s+"[^"]+"\))""","$1 (hide yes)");text=text.Remove(fp.SourceStart,fp.SourceLength).Insert(fp.SourceStart,edited);}if(!dryRun)File.WriteAllText(loaded.Data.File,text);
        return ToolResponse<BoardFinishingMutationResult>.Ok($"{(dryRun?"Previewed":"Applied")} silkscreen cleanup for {hide.Count} reference(s).",new("cleanup-silkscreen",string.Join(',',hide.Select(f=>f.Reference)),loaded.Data.File,dryRun,"hide overlapping references"));
    }

    public ToolResponse<BoardFinishingMutationResult> AddTestPoint(string projectPath, string reference, string net, double x, double y, double diameter, bool dryRun)
    {
        var loaded = Load(projectPath); if (!loaded.Success || loaded.Data is null) return Fail(loaded);
        if (diameter <= 0) return Error("Testpoint diameter must be positive.", "INVALID_MECHANICAL_GEOMETRY");
        var resolved = loaded.Data.Board.Nets.FirstOrDefault(item => item.Name.Equals(net, StringComparison.OrdinalIgnoreCase)); if (resolved is null) return Error($"Net not found: {net}", "NET_NOT_FOUND");
        var uuid = Guid.NewGuid().ToString();
        var text = $"\n\t(footprint \"PCBHelper:TestPoint\" (layer \"F.Cu\") (at {F(x)} {F(y)}) (uuid \"{uuid}\")\n\t\t(property \"Reference\" \"{Escape(reference)}\" (at 0 -2 0) (layer \"F.SilkS\"))\n\t\t(property \"Value\" \"TestPoint\" (at 0 2 0) (layer \"F.Fab\") (hide yes))\n\t\t(pad \"1\" thru_hole circle (at 0 0) (size {F(diameter)} {F(diameter)}) (drill {F(diameter / 2)}) (layers \"*.Cu\" \"*.Mask\") (net {resolved.Code} \"{Escape(resolved.Name)}\"))\n\t)";
        return Insert(loaded.Data, "add-testpoint", reference, text, dryRun);
    }

    public ToolResponse<BoardFinishingMutationResult> AddMountingHole(string projectPath, string reference, double x, double y, double drill, double diameter, bool dryRun)
    {
        var loaded = Load(projectPath); if (!loaded.Success || loaded.Data is null) return Fail(loaded);
        if (drill <= 0 || diameter < drill) return Error("Mounting-hole diameter must be at least its positive drill size.", "INVALID_MECHANICAL_GEOMETRY");
        var text = $"\n\t(footprint \"PCBHelper:MountingHole\" (layer \"F.Cu\") (at {F(x)} {F(y)}) (uuid \"{Guid.NewGuid()}\")\n\t\t(property \"Reference\" \"{Escape(reference)}\" (at 0 -3 0) (layer \"F.SilkS\"))\n\t\t(property \"Value\" \"MountingHole\" (at 0 3 0) (layer \"F.Fab\") (hide yes))\n\t\t(pad \"\" np_thru_hole circle (at 0 0) (size {F(diameter)} {F(diameter)}) (drill {F(drill)}) (layers \"*.Cu\" \"*.Mask\"))\n\t)";
        return Insert(loaded.Data, "add-mounting-hole", reference, text, dryRun);
    }

    public ToolResponse<BoardFinishingMutationResult> AddMechanicalKeepout(string projectPath, string layer, string points, bool dryRun)
    {
        var loaded = Load(projectPath); if (!loaded.Success || loaded.Data is null) return Fail(loaded);
        if (layer is not ("F.Cu" or "B.Cu")) return Error("Only F.Cu and B.Cu keep-outs are supported.", "UNSUPPORTED_LAYER");
        var polygon = ParsePoints(points); if (polygon is null || polygon.Count < 3) return Error("A keep-out requires at least three valid points.", "INVALID_MECHANICAL_GEOMETRY");
        var id = Guid.NewGuid().ToString();
        var text = $"\n\t(zone (net 0) (net_name \"\") (layer \"{layer}\") (uuid \"{id}\") (hatch edge 0.5)\n\t\t(keepout (tracks not_allowed) (vias not_allowed) (pads not_allowed) (copperpour not_allowed) (footprints not_allowed))\n\t\t(polygon (pts {string.Join(' ', polygon.Select(p => $"(xy {F(p.X)} {F(p.Y)})"))}))\n\t)";
        return Insert(loaded.Data, "add-mechanical-keepout", id, text, dryRun);
    }

    public ToolResponse<BoardFinishingMutationResult> RefillZones(string projectPath) => Error("KiCad CLI does not expose zone refill. Refill in KiCad and save before release.", "KICAD_ZONE_REFILL_UNAVAILABLE");

    private ToolResponse<BoardFinishingMutationResult> EditReference(string projectPath, string reference, bool dryRun, Func<string,string> edit, string operation)
    {
        var loaded = Load(projectPath); if (!loaded.Success || loaded.Data is null) return Fail(loaded);
        var fp = loaded.Data.Board.Footprints.FirstOrDefault(f => string.Equals(f.Reference, reference, StringComparison.OrdinalIgnoreCase)); if (fp is null) return Error($"Footprint not found: {reference}", "FOOTPRINT_NOT_FOUND");
        var before = loaded.Data.Text.Substring(fp.SourceStart, fp.SourceLength); var after = edit(before); if (after == before) return Error("Reference text could not be changed.", "REFERENCE_TEXT_NOT_FOUND");
        return Replace(loaded.Data, operation, reference, fp.SourceStart, fp.SourceLength, after, dryRun);
    }
    private static ToolResponse<BoardFinishingMutationResult> Insert(LoadedBoard loaded, string operation, string id, string objectText, bool dryRun)
    { var index = loaded.Text.LastIndexOf(')'); return Replace(loaded, operation, id, index, 0, objectText + "\n", dryRun); }
    private static ToolResponse<BoardFinishingMutationResult> Replace(LoadedBoard loaded, string operation, string id, int start, int length, string replacement, bool dryRun)
    { var after = loaded.Text.Remove(start, length).Insert(start, replacement); if (!dryRun) File.WriteAllText(loaded.File, after); return ToolResponse<BoardFinishingMutationResult>.Ok($"{(dryRun ? "Previewed" : "Applied")} {operation}.", new(operation, id, loaded.File, dryRun, replacement)); }
    private ToolResponse<LoadedBoard> Load(string projectPath) { var p = _projects.GetSummary(projectPath); if (!p.Success || p.Data?.BoardFile is null) return ToolResponse<LoadedBoard>.Fail(p.Summary, p.Error?.Code ?? "BOARD_NOT_FOUND", p.Error?.Message); var b=KiCadBoardParser.Parse(p.Data.BoardFile); return ToolResponse<LoadedBoard>.Ok("Loaded board.", new(p.Data.BoardFile,b.Text,b)); }
    private static ToolResponse<BoardFinishingMutationResult> Fail(ToolResponse<LoadedBoard> r) => Error(r.Summary, r.Error?.Code ?? "BOARD_NOT_FOUND", r.Error?.Message);
    private static ToolResponse<BoardFinishingMutationResult> Error(string summary,string code,string? message=null)=>ToolResponse<BoardFinishingMutationResult>.Fail(summary,code,message);
    private static List<(double X,double Y)>? ParsePoints(string value) { try { return value.Split(';',StringSplitOptions.RemoveEmptyEntries).Select(item=>item.Split(',',StringSplitOptions.TrimEntries)).Select(v=>(double.Parse(v[0],CultureInfo.InvariantCulture),double.Parse(v[1],CultureInfo.InvariantCulture))).ToList(); } catch { return null; } }
    private static (int Start,int Length,string Text)? FindBlock(string text,string kind,string id) { var i=0; while((i=text.IndexOf("("+kind,i,StringComparison.Ordinal))>=0){var e=FindEnd(text,i);if(e<0)return null;var b=text.Substring(i,e-i+1);if(b.Contains(id,StringComparison.OrdinalIgnoreCase))return(i,e-i+1,b);i=e+1;}return null; }
    private static int FindEnd(string text,int start){var d=0;var q=false;for(var i=start;i<text.Length;i++){if(text[i]=='\"'&&(i==0||text[i-1]!='\\'))q=!q;if(q)continue;if(text[i]=='(')d++;else if(text[i]==')'&&--d==0)return i;}return-1;}
    private static string ReplaceFirst(string input, string pattern, string replacement) => new Regex(pattern).Replace(input, replacement, 1);
    private static string F(double v)=>v.ToString("0.####",CultureInfo.InvariantCulture); private static string Escape(string v)=>v.Replace("\\","\\\\").Replace("\"","\\\"");
    private sealed record LoadedBoard(string File,string Text,KiCadBoardDocument Board);
}
public sealed record BoardFinishingMutationResult(string Operation,string ItemId,string ChangedFile,bool DryRun,string ProposedText);
