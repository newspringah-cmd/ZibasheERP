// Syntax only. A username never proves ownership; the server must use a verified stable Telegram user ID.
export function normalizeLoginTelegram(input:string):{kind:'username'|'numeric-id';value:string}|null {
 if(input.length>65)return null;
 const value=input.trim().replace(/[۰-۹]/g,c=>String(c.charCodeAt(0)-'۰'.charCodeAt(0)))
  .replace(/[٠-٩]/g,c=>String(c.charCodeAt(0)-'٠'.charCodeAt(0)));
 if(/^[0-9]+$/.test(value)){
  const id=BigInt(value);
  return id>0n&&id<=9223372036854775807n?{kind:'numeric-id',value:id.toString()}:null;
 }
 const username=value.startsWith('@')?value.slice(1):value;
 // Short legacy/collectible names are accepted syntactically; only the directory decides whether one exists.
 return /^[A-Za-z][A-Za-z0-9_]{0,31}$/.test(username)?{kind:'username',value:username.toLowerCase()}:null;
}
