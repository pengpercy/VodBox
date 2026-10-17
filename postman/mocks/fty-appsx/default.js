// Anonymous non-session v119 fixture; RSA/session and captcha flows are outside this fixture.
const http = require('http');
const crypto = require('crypto');
const key = Buffer.from('0123456789abcdef'); // public test key
function encrypt(text) { const cipher = crypto.createCipheriv('aes-128-cbc', key, key); return Buffer.concat([cipher.update(text), cipher.final()]).toString('base64'); }
const vod = {vod_id: '7', vod_name: '中文电影', vod_pic: 'cover.jpg', vod_class: '动作', vod_year: '2026', vod_area: '大陆', vod_remarks: '全集', vod_actor: '演员', vod_director: '导演', vod_blurb: '剧情'};
http.createServer(async (req, res) => {
  const route = req.url.split('?')[0];
  const reply = (code, value) => { res.writeHead(code, {'content-type': 'application/json'}); res.end(JSON.stringify(value)); };
  let raw = '';
  if (req.body !== undefined) raw = typeof req.body === 'string' ? req.body : new URLSearchParams(req.body).toString();
  else for await (const chunk of req) raw += chunk;
  const fields = Object.fromEntries(new URLSearchParams(raw));
  if (route === '/parse') return reply(200, {json: JSON.stringify({data: {url: 'https://media.example/external.mp4'}})});
  const time = req.headers['app-api-verify-time'];
  if (req.method !== 'POST' || req.headers['app-api-verify-sign'] !== encrypt(String(time)) || req.headers['app-version-code'] !== '113' || req.headers['user-agent'] !== 'okhttp/3.14.9') return reply(400, {error: 'v119 header contract'});
  const respond = data => reply(200, {code: 1, msg: 'success', data: encrypt(JSON.stringify(data))});
  const prefix = '/api.php/getappapi.index/';
  if (!route.startsWith(prefix)) return reply(501, {error: 'unknown route'});
  switch (route.slice(prefix.length)) {
    case 'initV119': return respond({type_list: [{type_id: 1, type_name: '电影', type_extend: JSON.stringify({class: '动作,喜剧', area: '大陆,香港', year: '2026,2025', lang: '国语'}), recommend_list: [vod], filter_type_list: []}]});
    case 'typeFilterVodList':
      if (!fields.type_id || !fields.page || !fields.area || !fields.year || !fields.class || !fields.lang || !fields.sort) return reply(400, {error: 'filter contract'});
      return respond({recommend_list: Number(fields.page) === 1 ? [vod] : []});
    case 'searchList':
      if (!fields.keywords || fields.type_id !== '0' || fields.page !== '1') return reply(400, {error: 'search contract'});
      return respond({search_list: [vod]});
    case 'vodDetail':
      return respond({vod, vod_play_list: [0, 1, 2].flatMap(type => [0, 1, 2].map(parseType => ({
        player_info: {show: `type${type}-parse${parseType}`, parse: `http://${req.headers.host}/parse?url=`, player_parse_type: String(type), parse_type: String(parseType), user_agent: 'media-agent', headers: {Referer: 'https://watch.example/'}},
        urls: [{name: '第一集', url: type === 0 ? 'https://media.example/direct.mp4' : 'https://watch.example/7', token: 'fixture-token'}]
      })))});
    case 'vodParse':
      if (!fields.url || fields.token !== 'fixture-token') return reply(400, {error: 'vodParse contract'});
      return respond({play_url: 'https://media.example/internal.mp4', url: 'https://media.example/internal.mp4'});
    default: return reply(501, {error: 'route outside fixture'});
  }
}).listen(process.env.PORT || 4903, '127.0.0.1');
