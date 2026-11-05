// Same production call path as probe_qoder.mjs, but dumps the full error stack
// so we can tell a real pi-ai bug from a probe artifact (no baseUrl => OpenAI
// SDK defaults to api.openai.com, which is not reachable from here).
const HOST_TRANSCRIPT = 'file:///D:/Deepseek%20Harness/resources/app/dsh/node_modules/@earendil-works/pi-ai/dist/utils/transcript.js';
const PROFILE_MODELS = 'file:///C:/Users/chenk/.dsh/profiles/desktop/node_modules/@earendil-works/pi-ai/dist/models.js';

const { normalizeContext } = await import(HOST_TRANSCRIPT);
const { createProvider } = await import(PROFILE_MODELS);
const { openAICompletionsApi } = await import(
  'file:///C:/Users/chenk/.dsh/profiles/desktop/node_modules/@earendil-works/pi-ai/dist/api/openai-completions.lazy.js'
);

const context = {
  systemPrompt: 'You are a coding assistant.',
  tools: [{ name: 'read_file', description: 'read a file', parameters: {} }],
  messages: [{ role: 'user', content: [{ type: 'text', text: 'hello' }], timestamp: 1 }],
};
const transcript = normalizeContext(context);

const base = process.argv[2] ?? 'http://127.0.0.1:1'; const omitBase = process.argv[3] === 'nobase';
const model = {
  id: 'DeepSeek-Flash', name: 'DeepSeek-Flash', provider: 'qoder', api: 'openai-completions',
  contextWindow: 128000, maxTokens: 8192, input: ['text'], baseUrl: `${base}/v1`,
};

const provider = createProvider({
  id: 'qoder', name: 'qoder',
  auth: { apiKey: { name: 'Qoder loopback shim token', async resolve() { return undefined; } } },
  models: [model],
  api: openAICompletionsApi(),
});

const out = await provider.streamSimple(model, transcript, {
  apiKey: 'sk-placeholder', maxTokens: 8192, temperature: 0.6,
});
for await (const ev of out) {
  if (ev.type === 'error' || ev.stopReason === 'error') console.log('event:', JSON.stringify(ev).slice(0, 400));
}
const final = await out.result();
console.log('baseUrl  :', base);
console.log('stopReason:', final.stopReason);
console.log('message   :', final.errorMessage ?? '(none)');
console.log('error obj :', final.error && typeof final.error === 'object'
  ? `${final.error.constructor?.name} | message=${JSON.stringify(final.error.message)} | keys=${Object.keys(final.error).join(',')}`
  : String(final.error));
console.log('stack     :\n' + (final.error?.stack ?? '(no stack on error)'));
