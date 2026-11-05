// Reports which pi-ai copy each plugin resolves, by walking the node_modules
// chain the way a bare-specifier import does. pi-ai has no CommonJS "main"
// export, so require.resolve cannot be used here.
import fs from 'node:fs';
import path from 'node:path';

const root = 'C:\\Users\\chenk\\.dsh\\profiles\\desktop\\node_modules';
const target = path.join('@earendil-works', 'pi-ai');

for (const pkg of ['@eghrhegpe/dsh-connect-qoder', '@opencode2dsh/dsh-plugin', 'dsh-connect-workbuddy', 'dsh-workbuddy-connect']) {
  let dir = path.join(root, pkg);
  let found;
  for (;;) {
    const candidate = path.join(dir, 'node_modules', target);
    if (fs.existsSync(path.join(candidate, 'package.json'))) { found = candidate; break; }
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  if (!found) { console.log(`${pkg.padEnd(34)} -> NOT FOUND`); continue; }
  const { version } = JSON.parse(fs.readFileSync(path.join(found, 'package.json'), 'utf8'));
  const nested = found.startsWith(path.join(root, pkg));
  console.log(`${pkg.padEnd(34)} -> ${version.padEnd(8)} ${nested ? '(nested)' : '(hoisted root)'}  ${found.replace(root, '<profile>')}`);
}