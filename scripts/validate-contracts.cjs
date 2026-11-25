const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '..');
const toolRoot = process.env.VAIRAS_CONTRACT_TOOL_ROOT || path.join(__dirname, 'contract-tools');
const tool = createRequire(path.join(path.resolve(toolRoot), 'package.json'));
const YAML = tool('yaml');
const { Parser } = tool('@asyncapi/parser');
const Ajv = tool('ajv/dist/2020').default;
const formats = tool('ajv-formats');
const read = name => fs.readFileSync(path.join(root, name), 'utf8').replace(/^\uFEFF/, '');
const check = (condition, message) => { if (!condition) throw Error(message); };
(async () => {
  const text = read('api/notifications.asyncapi.yaml');
  const parsed = await new Parser().parse(text);
  const errors = parsed.diagnostics.filter(d => d.severity === 0);
  check(parsed.document && errors.length === 0, JSON.stringify(errors));
  const contract = YAML.parse(text);
  const ajv = new Ajv({ strict: false, allErrors: true }); formats(ajv);
  function resolve(value, document) {
    if (Array.isArray(value)) return value.map(v => resolve(v, document));
    if (value && typeof value === 'object') {
      if (value.$ref) return resolve(value.$ref.slice(2).split('/').reduce((o, p) => o[p], document), document);
      return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, resolve(v, document)]));
    }
    return value;
  }
  let eventCount = 0;
  const samplePath = 'evidence/source-event-samples.json';
  check(fs.existsSync(path.join(root, samplePath)), 'Run the sibling-aware integration suite to capture source events.');
  const captured = JSON.parse(read(samplePath));
  for (const event of captured.events) {
    const message = contract.components.messages[event.headers.eventType]; check(message, 'Missing event schema.');
    for (const [schema, value] of [[message.headers, event.headers], [message.payload, event.payload]]) {
      const validate = ajv.compile(resolve(schema, contract));
      check(validate(value), `${event.headers.eventType}: ${JSON.stringify(validate.errors)}`);
    }
    eventCount++;
  }
  const openapi = JSON.parse(read('api/notifications.openapi.json'));
  const http = read('Vairas.Notifications/Vairas.Notifications.http');
  const covered = new Set(); let bodies = 0;
  const variables = Object.fromEntries([...http.matchAll(/^@(\w+)\s*=\s*(.*)$/gm)].map(m => [m[1], m[2].trim()]));
  const replace = s => s.replace(/\{\{(\w+)\}\}/g, (_, name) => variables[name] || name);
  for (const block of http.split(/^###.*$/m)) {
    const request = block.match(/^(GET|POST|PUT|PATCH|DELETE)\s+(.+)$/m); if (!request) continue;
    const method = request[1].toLowerCase(); const pathname = new URL(replace(request[2]).trim()).pathname;
    const template = Object.keys(openapi.paths).find(p => new RegExp('^' + p.replace(/\{[^}]+\}/g, '[^/]+') + '$').test(pathname));
    check(template && openapi.paths[template][method], `Unknown HTTP example: ${method} ${pathname}`);
    covered.add(`${method} ${template}`);
    const body = block.match(/\r?\n\r?\n\s*(\{[\s\S]*\})\s*$/); if (!body) continue;
    const schema = openapi.paths[template][method].requestBody?.content?.['application/json']?.schema;
    check(schema, 'HTTP body has no request schema.');
    const validate = ajv.compile(resolve(schema, openapi)); check(validate(JSON.parse(replace(body[1]))), JSON.stringify(validate.errors)); bodies++;
  }
  const operations = Object.entries(openapi.paths).flatMap(([p, item]) => Object.keys(item).filter(k => ['get', 'post', 'put', 'patch', 'delete'].includes(k)).map(k => `${k} ${p}`));
  check(operations.every(o => covered.has(o)), 'Missing HTTP operation examples.');
  const result = { asyncapiErrors: errors.length, asyncapiWarnings: parsed.diagnostics.length - errors.length, capturedEventsValidated: eventCount,
    httpOperationsCovered: covered.size, implementedOperations: operations.length, requestBodiesValidated: bodies };
  fs.writeFileSync(path.join(root, 'evidence/contracts-validation.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result));
})().catch(error => { console.error(error.message); process.exitCode = 1; });
