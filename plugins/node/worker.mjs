import { createInterface } from 'node:readline';
import { pathToFileURL } from 'node:url';
const write = process.stdout.write.bind(process.stdout);
console.log = console.info = (...args) => console.error(...args);
const provider = await import(pathToFileURL(process.argv[2]).href);
for await (const line of createInterface({ input: process.stdin, crlfDelay: Infinity })) {
  let requestId = 0;
  try {
    const request = JSON.parse(line); requestId = request.requestId;
    if (request.apiVersion !== 1) throw new Error('Unsupported apiVersion');
    const target = provider.default ?? provider;
    const method = target[request.method];
    if (typeof method !== 'function') throw new Error('Unknown provider method');
    const result = await method.call(target, request.params);
    write(JSON.stringify({ apiVersion: 1, requestId, result: result ?? {} }) + '\n');
  } catch (error) { write(JSON.stringify({ apiVersion: 1, requestId, error: String(error) }) + '\n'); }
}
