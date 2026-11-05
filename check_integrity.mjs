// Final integrity check: every pi-ai copy in the profile must still import
// cleanly (opencode2dsh keeps its own nested 0.82.1 and its own provider path).
import { pathToFileURL } from 'node:url';

const copies = [
  ['root (qoder/workbuddy)', 'C:/Users/chenk/.dsh/profiles/desktop/node_modules/@earendil-works/pi-ai/dist/models.js'],
  ['nested (opencode2dsh)', 'C:/Users/chenk/.dsh/profiles/desktop/node_modules/@opencode2dsh/dsh-plugin/node_modules/@earendil-works/pi-ai/dist/models.js'],
];

for (const [label, modelsPath] of copies) {
  const base = modelsPath.replace(/\/models\.js$/, '');
  const url = (p) => pathToFileURL(p).href;
  try {
    const m = await import(url(modelsPath));
    const oc = await import(url(`${base}/api/openai-completions.js`));
    const est = await import(url(`${base}/utils/estimate.js`));
    // Each copy must handle the shape its own callers pass. Nested 0.82.1 is
    // driven by opencode2dsh's legacy {systemPrompt, tools, messages} context;
    // root 0.87.1 is driven by the host's transcript shape.
    const tryEstimate = (ctx) => {
      try { return JSON.stringify(est.estimateContextTokens(ctx)); }
      catch (e) { return `THROWS: ${e.message}`; }
    };
    const legacy = { systemPrompt: 'sys', tools: [{ name: 't' }], messages: [{ role: 'user', content: [{ type: 'text', text: 'hi' }], timestamp: 1 }] };
    const tr = { messages: [{ role: 'system', content: 'sys', timestamp: 0 }, { role: 'user', content: [{ type: 'text', text: 'hi' }], timestamp: 1 }] };
    console.log(`${label.padEnd(24)} import OK | createProvider=${typeof m.createProvider} | openai-completions keys=${Object.keys(oc).length}`);
    console.log(`${''.padEnd(24)} legacy-shape    : ${tryEstimate(legacy)}`);
    console.log(`${''.padEnd(24)} transcript-shape : ${tryEstimate(tr)}`);
  } catch (e) {
    console.log(`${label.padEnd(24)} FAILED: ${e.message}`);
  }
}