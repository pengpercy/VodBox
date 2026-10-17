const http = require('http');
const crypto = require('crypto');
const key = Buffer.from('KANGEQIU@8868!~.');
const iv = Buffer.from('0200010900030207');
const game = {mid: 100, match_id: 200, ball_type: 1, m_status: 1, event_name: '测试联赛', stage: '第一轮', match_time: 1792250000,
  home: {name: '主队', logo: 'https://media.example/home.png', score: '2'}, visiting: {name: '客队', score: '1'}};
http.createServer(async (req, res) => {
  const route = req.url.startsWith('/gz/') ? req.url : '/gz' + req.url;
  const reply = (status, value) => { res.writeHead(status, {'content-type': 'application/json'}); res.end(JSON.stringify(value)); };
  let raw = '';
  if (req.body !== undefined) raw = typeof req.body === 'string' ? req.body : new URLSearchParams(req.body).toString();
  else for await (const chunk of req) raw += chunk;
  if (req.method !== 'POST' || req.headers['user-agent'] !== 'okhttp/3.12.0' || req.headers['client-version'] !== '3.0.4.0') return reply(400, {error: 'headers'});
  const encoded = new URLSearchParams(raw).get('parameter');
  let data;
  try { const cipher = crypto.createDecipheriv('aes-128-cbc', key, iv); data = JSON.parse(Buffer.concat([cipher.update(Buffer.from(encoded, 'base64')), cipher.final()]).toString()); }
  catch { return reply(400, {error: 'encrypted parameter'}); }
  let value;
  if (route === '/gz/live/sports?parameter=key') {
    if (JSON.stringify(data) !== '{"frame":"0","hot":"1","tag":"0","type":"0"}') return reply(400, {error: 'schedule contract'});
    value = [game, {...game, mid: 101, m_status: 0}, {...game, mid: 102, m_status: 2}];
  } else if (route === '/gz/live/detail?parameter=key') {
    if (JSON.stringify(data) !== '{"ball_type":"1","match_id":"200","mid":"100"}') return reply(400, {error: 'detail contract'});
    value = {...game, live_line: [{name: '线路一', m3u8: 'https://media.example/live.m3u8'}, {name: '线路二', m3u8: 'https://media.example/live2.m3u8'}]};
  } else return reply(501, {error: 'route outside fixture'});
  const cipher = crypto.createCipheriv('aes-128-cbc', key, iv);
  return reply(200, {data: Buffer.concat([cipher.update(JSON.stringify(value)), cipher.final()]).toString('base64')});
}).listen(process.env.PORT || 4906, '127.0.0.1');
