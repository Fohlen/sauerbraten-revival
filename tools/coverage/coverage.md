# Collecting Code Coverage with Godot Mono & dotnet-coverage

Godot Mono is a native executable that internally hosts CoreCLR, so standard dynamic profiling (`dotnet coverage collect -- Godot ...`) fails with *"Profiler wurde nicht initialisiert"* (profiler not initialized).

To collect coverage, statically instrument the compiled game assembly before running the tests, then restore (uninstrument) it afterward.

### 1. Build the Assembly
```sh
dotnet build SauerbratenRevival.sln

### 2. Instrument, Collect & Uninstrument (One-Liner)
DLL_PATH=".godot/mono/temp/bin/Debug/SauerbratenRevival.dll"
GODOT="/Applications/Godot_mono.app/Contents/MacOS/Godot"
SUITE="WorldSpatialIndexTest"

dotnet coverage instrument "$DLL_PATH" && \
SAUER_REF=/Users/fohlen/Downloads/sauerbraten-code-r6889 \
dotnet coverage collect -f xml -o coverage.xml -- \
    "$GODOT" --headless --path . --run-tests="$SUITE" --quit-on-finish; \
dotnet coverage uninstrument "$DLL_PATH"

Note: You can also use -f cobertura instead of -f xml for standard CI Cobertura XML.

### 3. Quick Terminal Coverage Summary (Python)

python3 -c "
import xml.etree.ElementTree as ET
tree = ET.parse('coverage.xml')
for f in tree.findall('.//function'):
    if f.get('type_name') == 'WorldSpatialIndex':
        print(f\"{f.get('name'):55} : {f.get('line_coverage'):>6}% (cov={f.get('lines_covered')}, not={f.get('lines_not_covered')})\")
"
```
