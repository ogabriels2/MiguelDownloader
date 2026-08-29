const fs = require('fs');
const path = require('path');

const decode = value => value
  .replace(/&lt;/g, '<')
  .replace(/&gt;/g, '>')
  .replace(/&quot;/g, '"')
  .replace(/&apos;/g, "'")
  .replace(/&amp;/g, '&');

function read(file) {
  const xml = fs.readFileSync(file, 'utf8');
  const values = new Map();
  const expression = /<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>\s*<\/data>/g;

  for (const match of xml.matchAll(expression)) {
    if (values.has(match[1])) throw new Error(`duplicate resource key ${match[1]} in ${file}`);
    values.set(match[1], decode(match[2]));
  }

  return values;
}

const pt = read(process.argv[2]);
const en = read(process.argv[3]);

for (const key of pt.keys()) {
  if (!en.has(key)) throw new Error(`English resource is missing ${key}`);
}
for (const key of en.keys()) {
  if (!pt.has(key)) throw new Error(`Portuguese resource is missing ${key}`);
}

const table = Object.fromEntries([...pt].map(([key, value]) => [key, [value, en.get(key)]]));
const output = process.argv[4];
fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output, JSON.stringify(table, null, 2) + '\n', 'utf8');
console.log(`imported ${pt.size} strings from 2 resx files`);
