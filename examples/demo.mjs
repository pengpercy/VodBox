let options = {};
const card = { id: 'sample', title: 'Node.js 插件示例', remarks: '独立进程 NDJSON' };
export default {
  init(value) { options = value; return {}; },
  categories() { return [{ id: 'demo', name: '协议示例' }]; },
  items() { return { items: [card], nextCursor: null }; },
  search(args) { return { items: card.title.toLowerCase().includes((args.query ?? '').toLowerCase()) ? [card] : [] }; },
  detail() { return { item: card, description: '在配置 options.mediaUri 中填写自己的媒体地址。', playbackLines: [{ id: 'main', name: '主线路', episodes: [{ id: 'main', title: '播放' }] }] }; },
  resolvePlayback() { if (!options.mediaUri) throw new Error('请先配置 options.mediaUri'); return { uri: options.mediaUri, title: card.title }; }
};
