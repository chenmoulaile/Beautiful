// Replicates the exact production call path:
//   dsh-llm-pi-ai  ->  host ModelsImpl.streamSimple  ->  normalizeContext  ->
//   plugin provider (profile pi-ai 0.82.1) .streamSimple  ->  buildBaseOptions
//   ->  clampMaxTokensToContext  ->  estimateContextTokens  ->  CRASH
const HOST = 'file:///D:/Deepseek%20Harness/resources/app/dsh/node_modules/@earendil-works/pi-ai/dist/models.js';
const HOST_EST = 'file:///D:/Deepseek%20Harness/resources/app/dsh/node_modules/@earendil-works/pi-ai/dist/utils/estimate.js';
const HOST_TRANSCRIPT = 'file:///D:/Deepseek%20Harness/resources/app/dsh/node_modules/@earendil-works/pi-ai/dist/utils/transcript.js';
const PROFILE_OC = 'file:///C:/Users/chenk/.dsh/profiles/desktop/node_modules/@earendil-works/pi-ai/dist/api/openai-completions.js';
const PROFILE_MODELS = 'file:///C:/Users/chenk/.dsh/profiles/desktop/node_modules/@earendil-works/pi-ai/dist/models.js';

const { normalizeContext } = await import(HOST_TRANSCRIPT);
const { createProvider } = await import(PROFILE_MODELS);
const { openAICompletionsApi } = await import(
  'file:///C:/Users/chenk/.dsh/profiles/desktop/node_modules/@earendil-works/pi-ai/dist/api/openai-completions.lazy.js'
);

// The context dsh-llm-pi-ai builds: { systemPrompt, tools, messages }.
const context = {
  systemPrompt: 'You are a coding assistant.',
  tools: [{ name: 'read_file', description: 'read a file', parameters: {} }],
  messages: [{ role: 'user', content: [{ type: 'text', text: 'hello' }], timestamp: 1 }],
};

const model = {
  id: 'DeepSeek-Flash', name: 'DeepSeek-Flash', provider: 'qoder', api: 'openai-completions',
  contextWindow: 128000, maxTokens: 8192, input: ['text'],
};

// Host normalizes to transcript shape, then hands it to the plugin-built provider.
const transcript = normalizeContext(context);
console.log('host normalizeContext ->', JSON.stringify(transcript.messages[0]));

// Exactly what the plugin registers (lib/index.js:1296-1311): auth.apiKey is an
// object with resolve(), and the adapter supplies the key via streamSimple options.
const provider = createProvider({
  id: 'qoder', name: 'qoder',
  auth: { apiKey: { name: 'Qoder loopback shim token', async resolve() { return undefined; } } },
  models: [model],
  api: openAICompletionsApi(),
});

let out;
try {
  out = await provider.streamSimple(model, transcript, {
    apiKey: 'sk-placeholder', maxTokens: 8192, temperature: 0.6,
  });
  const final = await out.result();
  console.log('RESULT stopReason =', final.stopReason, '| errorMessage =', final.errorMessage ?? '(none)');
} catch (e) {
  console.log('THROWN SYNC:', e.constructor.name, '|', e.message);
}

console.log('\n--- isolated: which pi-ai estimate impl runs ---');
const hostEst = await import(HOST_EST);
const profileEst = await import(
  'file:///C:/Users/chenk/.dsh/profiles/desktop/node_modules/@earendil-works/pi-ai/dist/utils/estimate.js'
);
for (const [label, mod] of [['host 0.87.1', hostEst], ['profile 0.82.1', profileEst]]) {
  try {
    console.log(label, '->', JSON.stringify(mod.estimateContextTokens(transcript)));
  } catch (e) {
    console.log(label, '-> THROWN:', e.message);
  }
}
