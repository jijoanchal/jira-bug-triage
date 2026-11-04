/** Parses the small markup used in bug descriptions ("## heading", "- bullet", "1. item", ``` code ```). */
export type Block =
  | { type: 'h'; text: string }
  | { type: 'p'; text: string }
  | { type: 'ul' | 'ol'; items: string[] }
  | { type: 'code'; text: string };

export function parseMarkup(text: string): Block[] {
  const blocks: Block[] = [];
  const lines = (text ?? '').replace(/\r\n/g, '\n').split('\n');
  let para: string[] = [];
  let list: { type: 'ul' | 'ol'; items: string[] } | null = null;

  const flushPara = () => {
    if (para.length) blocks.push({ type: 'p', text: para.join(' ') });
    para = [];
  };
  const flushList = () => {
    if (list) blocks.push(list);
    list = null;
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (line.trimStart().startsWith('```')) {
      flushPara(); flushList();
      const code: string[] = [];
      for (i++; i < lines.length && !lines[i].trimStart().startsWith('```'); i++) code.push(lines[i]);
      blocks.push({ type: 'code', text: code.join('\n') });
      continue;
    }
    const h = /^\s*#{1,6}\s+(.+?)\s*$/.exec(line);
    if (h) { flushPara(); flushList(); blocks.push({ type: 'h', text: h[1] }); continue; }
    const b = /^\s*[-*]\s+(.+)$/.exec(line);
    const n = /^\s*\d+[.)]\s+(.+)$/.exec(line);
    if (b || n) {
      flushPara();
      const type = b ? 'ul' : 'ol';
      if (list && list.type !== type) flushList();
      list ??= { type, items: [] };
      list.items.push((b ?? n)![1]);
      continue;
    }
    if (!line.trim()) { flushPara(); flushList(); continue; }
    flushList();
    para.push(line.trim());
  }
  flushPara(); flushList();
  return blocks;
}
