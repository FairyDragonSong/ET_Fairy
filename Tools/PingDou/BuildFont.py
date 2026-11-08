"""Optional font regeneration: pip install fonttools. Baked font is checked in."""
from pathlib import Path
from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

ROOT = Path(__file__).resolve().parents[2]
FONT = ROOT / "Unity/Assets/GameRes/Fonts/SourceHanSans/SourceHanSansSC-VF.otf"
OUTPUT = ROOT / "Unity/Assets/GameRes/PingDou/UI/PingDouSans.otf"
font = TTFont(FONT)
text = "".join(
    path.read_text(encoding="utf-8-sig")
    for folder in ["Unity/Assets/Scripts/ModelView/Client/PingDou", "Unity/Assets/Resources/PingDou", "Book/PingDou"]
    for path in (ROOT / folder).rglob("*")
    if path.suffix in [".cs", ".txt", ".json"]
)
options = subset.Options()
options.name_IDs = ["*"]
options.name_legacy = True
options.name_languages = ["*"]
subsetter = subset.Subsetter(options=options)
subsetter.populate(text=text + "".join(chr(i) for i in range(32, 127)) + "★☆✓✦↺− ×%颗分")
subsetter.subset(font)
instantiateVariableFont(font, {"wght": 550}, inplace=True)
# Preserve OFL notices while giving the derived font its own family name.
names = {1: "PingDou Sans", 2: "Medium", 3: "PingDouSans-Medium-1.0", 4: "PingDou Sans Medium",
         6: "PingDouSans-Medium", 16: "PingDou Sans", 17: "Medium", 25: "PingDouSans"}
for entry in font["name"].names:
    if entry.nameID in names:
        entry.string = names[entry.nameID].encode(entry.getEncoding())
font.save(OUTPUT)
print(f"Wrote {OUTPUT} ({OUTPUT.stat().st_size:,} bytes)")
