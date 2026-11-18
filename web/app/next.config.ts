import type { NextConfig } from 'next';
import {checkBuildEnvironment} from './scripts/check-isolation.mjs';

checkBuildEnvironment();

// This stage produces static synthetic files. No API, rewrites or server integrations.
const config: NextConfig = {
  output: 'export',
  trailingSlash: true,
  poweredByHeader: false,
  images: { unoptimized: true },
};
export default config;
