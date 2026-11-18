// Input assistance only. Server must normalize again and resolve an explicit verified binding.
export function normalizeLoginMobile(input:string):string|null {
 if(input.length>40)return null;
 let value=input.trim().replace(/[۰-۹]/g,c=>String(c.charCodeAt(0)-'۰'.charCodeAt(0)))
  .replace(/[٠-٩]/g,c=>String(c.charCodeAt(0)-'٠'.charCodeAt(0)));
 if(/[^0-9+ ()-]/.test(value))return null;
 value=value.replace(/[ ()-]/g,'');
 if(value.startsWith('+98'))value='0'+value.slice(3);
 else if(value.startsWith('0098'))value='0'+value.slice(4);
 return /^09[0-9]{9}$/.test(value)?value:null;
}
