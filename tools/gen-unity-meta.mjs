// Generates missing .meta files for the UPM package. Packages installed from a git URL are
// immutable, and Unity ignores any file in them that has no .meta file.
// GUIDs are derived from the file path, so they are stable and re-running is a no-op.
//
// Usage: node tools/gen-unity-meta.mjs [packageDir]
import { createHash } from "node:crypto";
import { existsSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const root = process.argv[2] ??
  join(dirname(fileURLToPath(import.meta.url)), "..", "unity-package", "com.letiarts.playtest-telemetry");

const guidFor = (path) => createHash("md5").update("com.letiarts.playtest-telemetry:" + path).digest("hex");

const importerTail = `  userData:
  assetBundleName:
  assetBundleVariant:
`;

function metaFor(path, isDir) {
  const guid = guidFor(path);
  const head = `fileFormatVersion: 2\nguid: ${guid}\n`;
  if (isDir) {
    return head + `folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n${importerTail}`;
  }
  if (path.endsWith(".cs")) {
    return head + `MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n${importerTail}`;
  }
  if (path.endsWith(".asmdef")) {
    return head + `AssemblyDefinitionImporter:\n  externalObjects: {}\n${importerTail}`;
  }
  if (path.endsWith(".mm")) {
    // Native iOS plugin: enabled for iOS only.
    return head + `PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 1
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  - first:
      iPhone: iOS
    second:
      enabled: 1
      settings:
        AddToEmbeddedBinaries: false
        CPU: AnyCPU
        CompileFlags:
        FrameworkDependencies:
${importerTail}`;
  }
  return head + `TextScriptImporter:\n  externalObjects: {}\n${importerTail}`;
}

let created = 0;
function walk(dir) {
  for (const name of readdirSync(dir)) {
    if (name.endsWith(".meta")) continue;
    if (name.endsWith("~")) continue; // Samples~ / Documentation~ are hidden from Unity and need no meta
    const full = join(dir, name);
    const rel = relative(root, full).replaceAll("\\", "/");
    const isDir = statSync(full).isDirectory();
    const metaPath = full + ".meta";
    if (!existsSync(metaPath)) {
      writeFileSync(metaPath, metaFor(rel, isDir), "utf8");
      created++;
    }
    if (isDir) walk(full);
  }
}

walk(root);
console.log(`created ${created} .meta file(s) in ${root}`);
