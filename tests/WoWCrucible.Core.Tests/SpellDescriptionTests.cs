using System.Text.Json;
using WoWCrucible.Core;

internal static class SpellDescriptionTests
{
    public static void Run(string? directory = null)
    {
        void Require(bool condition,string message) { if(!condition)throw new InvalidOperationException(message); }
        var fields=new Dictionary<(uint,string),object> {
            [(1,"EffectBasePoints[0]")]=147,[(1,"EffectDieSides[0]")]=1,[(1,"EffectAuraPeriod[0]")]=1000,
            [(1,"DurationIndex")]=35,[(2,"Name[enUS]")]="Catalyst",[(2,"Description[enUS]")]="Adds $s1% damage.",
            [(2,"EffectBasePoints[0]")]=3,[(2,"EffectDieSides[0]")]=1 };
        var renderer=new SpellDescriptionRenderer((id,col)=>fields.GetValueOrDefault((id,col)),(table,id,col)=>table=="SpellDuration"&&id==35&&col=="Duration"?8000:null);
        var text=renderer.Render("Deals ${($1m1)+($SP*0.16)+($AP*0.12)} damage every $t1 seconds for $d. |cFFFFFFFFCatalyst|r @s:2:1@",1);
        Require(text.Notices.Count==0 && text.Text.Contains("148 + 16% of spell power + 12% of attack power") && text.Text.Contains("every 1 seconds for 8 seconds") && text.Text.Contains("Catalyst: Adds 4% damage."),"Poison Rain formula/reference preview failed: "+text.Text);
        foreach(var source in new[]{"$unknown","$999999999999999999s1","${invalid($SP)}","$?a1[yes[bad]][no]","@re:3949:0@","|Hspell:2|hCatalyst|h","$999999s1","$donds","$lpoint:points;","$ghe:she;"})
            Require(!SpellDescriptionRenderer.HasInstructions(renderer.Render(source,1).Text),"Unrendered instruction: "+source);
        fields[(2,"Description[enUS]")]="@s:2:1@";
        Require(renderer.Render("@s:2:1@",1).Notices.Any(n=>n.Contains("Circular")),"Circular references must be bounded and reported.");
        Require(SpellTooltipService.Clean("|cffffffff A\r\n B |r")=="A B","Markup whitespace compatibility changed.");
        if(directory is not null)
        {
            var file=WdbcFile.Load(Path.Combine(directory,"Spell.dbc"));
            var related=new Dictionary<string,WdbcFile>();
            foreach(var name in new[]{"SpellDuration","SpellRadius"}) {var path=Path.Combine(directory,name+".dbc");if(File.Exists(path))related[name]=WdbcFile.Load(path);}
            renderer=SpellTooltipService.CreateRenderer(file,related);
            var schema=DbcSchemaCatalog.CreateBuiltIn12340().ResolveColumns("Spell",file.FieldCount);
            var idColumn=schema.Columns.Single(c=>c.Name=="ID");
            var columns=schema.Columns.Where(c=>c.Name is "Name[enUS]" or "NameSubtext[enUS]" or "Description[enUS]" or "AuraDescription[enUS]").ToArray();
            var notices=new Dictionary<string,int>();int count=0,incomplete=0;
            for(var row=0;row<file.RowCount;row++)foreach(var column in columns)
            {
                var raw=Convert.ToString(file.GetDisplayValue(row,column))??"";if(raw.Length==0)continue;
                var id=file.GetRaw(row,idColumn);var preview=renderer.Render(raw,id);count++;
                Require(!System.Text.RegularExpressions.Regex.IsMatch(preview.Text,@"\$(?:[A-Za-z@?<{]|\d+[A-Za-z])|@[A-Za-z]+:|\|(?:c[0-9A-Fa-f]{8}|[rHTAth])"),$"Raw instruction leaked: {id}/{column.Name}");
                if(preview.Notices.Count>0)incomplete++;
                foreach(var notice in preview.Notices)notices[notice]=notices.GetValueOrDefault(notice)+1;
                if(id==1587725 && column.Name=="Description[enUS]") {Console.WriteLine("POISON RAIN: "+preview.Text);Require(preview.Notices.Count==0,"Actual Poison Rain did not fully resolve.");}
            }
            Directory.CreateDirectory(".local/tooltip-audit");
            File.WriteAllText(".local/tooltip-audit/corpus.json",JsonSerializer.Serialize(new{count,incomplete,notices},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"TOOLTIP CORPUS: {count} nonempty fields; zero raw instruction leaks; {incomplete} previews explicitly report unavailable context or instructions.");
        }
        Console.WriteLine("SPELL DESCRIPTION TESTS PASSED");
    }
}
