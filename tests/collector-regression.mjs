import assert from 'node:assert/strict'
import { spawn, spawnSync } from 'node:child_process'
import { randomBytes, randomUUID } from 'node:crypto'
import { once } from 'node:events'
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'

const binary = process.argv[2]
assert.ok(binary, 'Usage: node tests/collector-regression.mjs /path/to/otelcol-contrib')
const source = resolve('src/Dashboard/otel-collector-config.yaml')
const environment = {
  ...process.env,
  APPLICATIONINSIGHTS_CONNECTION_STRING: `InstrumentationKey=${randomUUID()};IngestionEndpoint=http://127.0.0.1:1`,
}
const validation = spawnSync(binary, ['validate', '--config', source], { env: environment, encoding: 'utf8' })
assert.equal(validation.status, 0, validation.stderr || validation.error?.message)
const parsed = spawnSync('ruby', ['-ryaml', '-rjson', '-e', 'puts JSON.generate(YAML.load_file(ARGV[0]))', source], { encoding: 'utf8' })
assert.equal(parsed.status, 0, parsed.stderr || parsed.error?.message)
const config = JSON.parse(parsed.stdout)
const portProbe = createServer()
portProbe.listen(0, '127.0.0.1')
await once(portProbe, 'listening')
const port = portProbe.address().port
await new Promise(resolve => portProbe.close(resolve))
const directory = await mkdtemp(join(tmpdir(), 'finops-collector-test-'))
let collector
let exited
let diagnostics = ''
try {
  config.receivers.otlp.protocols = { http: { endpoint: `127.0.0.1:${port}` } }
  config.exporters = {}
  for (const [signal, pipeline] of Object.entries(config.service.pipelines)) {
    config.exporters[`file/${signal}`] = { path: join(directory, `${signal}.json`) }
    pipeline.exporters = [`file/${signal}`]
  }
  config.service.telemetry = { logs: { level: 'info' }, metrics: { level: 'none' } }
  const testConfig = join(directory, 'config.yaml')
  await writeFile(testConfig, JSON.stringify(config))
  collector = spawn(binary, ['--config', testConfig], { env: environment, stdio: ['ignore', 'pipe', 'pipe'] })
  collector.stderr.on('data', chunk => { diagnostics += chunk.toString() })
  exited = once(collector, 'exit')
  await new Promise((resolve, reject) => {
    let startup = ''
    const timeout = setTimeout(() => reject(new Error(`Collector startup timed out: ${startup}`)), 15000)
    collector.once('error', reject)
    collector.once('exit', code => {
      clearTimeout(timeout)
      reject(new Error(`Collector exited before readiness (${code}): ${startup}`))
    })
    collector.stderr.on('data', chunk => {
      startup += chunk.toString()
      if (startup.includes('Everything is ready.')) {
        clearTimeout(timeout)
        resolve()
      }
    })
  })
  const oversized = 'x'.repeat(12000)
  const unicode = String.fromCodePoint(0x1f600).repeat(6000)
  const attributes = [
    { key: 'gen_ai.input.messages', value: { stringValue: oversized } },
    { key: 'synthetic.unicode', value: { stringValue: unicode } },
    { key: 'gen_ai.usage.input_tokens', value: { intValue: '321' } },
  ]
  const resource = { attributes }
  const traceId = randomBytes(16).toString('hex')
  const spanId = randomBytes(8).toString('hex')
  const startTimeUnixNano = (BigInt(Date.now()) * 1000000n).toString()
  const endTimeUnixNano = (BigInt(startTimeUnixNano) + 1000000n).toString()
  const post = async (signal, payload) => {
    const response = await fetch(`http://127.0.0.1:${port}/v1/${signal}`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload), signal: AbortSignal.timeout(10000),
    })
    assert.equal(response.status, 200, await response.text())
  }
  await post('traces', { resourceSpans: [{ resource, scopeSpans: [{ spans: [{
    traceId, spanId, name: 'synthetic-regression', startTimeUnixNano, endTimeUnixNano,
    attributes, status: { code: 2 }, events: [{ name: 'synthetic', timeUnixNano: startTimeUnixNano, attributes }],
  }] }] }] })
  await post('logs', { resourceLogs: [{ resource, scopeLogs: [{ logRecords: [{
    traceId, spanId, timeUnixNano: startTimeUnixNano, severityNumber: 17,
    body: { stringValue: oversized }, attributes,
  }] }] }] })
  collector.kill('SIGTERM')
  const [exitCode] = await exited
  assert.equal(exitCode, 0, `Collector shutdown must flush all batches: ${diagnostics}`)
  const documents = []
  for (const signal of ['traces', 'logs']) {
    const content = await readFile(join(directory, `${signal}.json`), 'utf8')
    documents.push(...content.trim().split('\n').map(line => JSON.parse(line)))
  }
  const spans = documents.flatMap(document => document.resourceSpans ?? [])
  const logs = documents.flatMap(document => document.resourceLogs ?? [])
  assert.equal(spans.length, 1)
  assert.equal(logs.length, 1)
  const checkAttributes = values => {
    const byName = Object.fromEntries(values.map(attribute => [attribute.key, attribute.value]))
    assert.equal(byName['gen_ai.input.messages'].stringValue.length, 4096)
    assert.ok(byName['synthetic.unicode'].stringValue.length <= 4096)
    assert.ok(!byName['synthetic.unicode'].stringValue.includes('\ufffd'))
    assert.equal(Number(byName['gen_ai.usage.input_tokens'].intValue), 321)
  }
  const span = spans[0].scopeSpans[0].spans[0]
  const log = logs[0].scopeLogs[0].logRecords[0]
  for (const values of [spans[0].resource.attributes, span.attributes, span.events[0].attributes,
    logs[0].resource.attributes, log.attributes]) checkAttributes(values)
  assert.equal(span.traceId, traceId)
  assert.equal(span.spanId, spanId)
  assert.equal(span.startTimeUnixNano, startTimeUnixNano)
  assert.equal(span.endTimeUnixNano, endTimeUnixNano)
  assert.equal(span.status.code, 2)
  assert.equal(log.traceId, traceId)
  assert.equal(log.spanId, spanId)
  assert.equal(log.timeUnixNano, startTimeUnixNano)
  assert.equal(log.severityNumber, 17)
  assert.equal(log.body.stringValue, oversized)
  console.log('PASS: Production config validates; trace/log attributes are bounded; numbers, identity, timing and log body are preserved.')
} finally {
  if (collector && collector.exitCode === null && collector.signalCode === null) {
    collector.kill('SIGTERM')
    await exited
  }
  await rm(directory, { recursive: true, force: true })
}