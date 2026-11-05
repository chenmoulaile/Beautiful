import * as PR from 'file:///C:/Users/chenk/.dsh/profiles/desktop/node_modules/dsh-our-free-model/src/providers/index.js'
import { HttpsProxyAgent } from 'file:///C:/Users/chenk/.dsh/profiles/desktop/node_modules/@earendil-works/pi-ai/node_modules/https-proxy-agent/dist/index.js'
import https from 'node:https'

const rows = ['opencode_zen','opencode_go','kilo','github_copilot','siliconflow','glm-cn','minimax-cn','qwencloud','xiaomi-tokenplan','volcengine-ark','tencent','deepseek']
for (const id of rows) {
  const p = PR.getProvider(id)
  if (!p) { console.log(id.padEnd(18), '-> NOT FOUND'); continue }
  let chat = '', models = ''
  try { chat = PR.chatEndpoint(p) } catch (e) { chat = 'ERR ' + e.message }
  try { models = PR.modelsEndpoint(p) } catch (e) { models = 'ERR ' + e.message }
  console.log(`${id.padEnd(18)} auth=${(p.authKind ?? p.auth ?? '?')} base=${p.baseUrl ?? p.base ?? '?'} chat=${chat} models=${models}`)
}
