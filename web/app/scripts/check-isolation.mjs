import {readdirSync} from 'node:fs';
import {fileURLToPath} from 'node:url';

export function assertIsolation(env, files = []) {
  if (files.some(name => name.startsWith('.env') && name!=='.env.example'))
    throw new Error('Isolated web build refuses environment files');
  if (Object.entries(env).some(([key,value])=> value && (
    /^(ConnectionStrings__|NEXT_PUBLIC_|ZIBASHE_WEB_)/i.test(key) ||
    /^(DATABASE_URL|DB_CONNECTION_STRING|SQLCONNSTR_DefaultConnection)$/i.test(key))))
    throw new Error('Isolated web build refuses integration settings');
  if (['APP_ENV','ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT'].some(k=>env[k]?.toLowerCase()==='production'))
    throw new Error('Live application environment refused');
}
export function checkBuildEnvironment() {
  assertIsolation(process.env,readdirSync(fileURLToPath(new URL('../',import.meta.url))));
}
if (process.argv[1]===fileURLToPath(import.meta.url))checkBuildEnvironment();
