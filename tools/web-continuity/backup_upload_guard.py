"""Prepared upload guard; not installed on production. No credentials, retention or billing changes."""
class RemoteConflict(RuntimeError):pass
class RemoteVerificationFailed(RuntimeError):pass

def omit_expect_header(request,**kwargs):
    # Registered before signing: signature covers the final headers. TLS remains enabled.
    if 'Expect' in request.headers:del request.headers['Expect']

def verified_upload(client,bucket,path,key,metadata,transfer_config=None):
    """Reuse only the same key+size+hash. An uncertain write is reconciled, never blindly repeated."""
    if not metadata.get('sha256'):raise ValueError('SHA-256 metadata required')
    expected_size=path.stat().st_size
    def head():
        try:return client.head_object(Bucket=bucket,Key=key)
        except Exception as exc:
            response=getattr(exc,'response',{})
            if response.get('Error',{}).get('Code') in ['404','NoSuchKey','NotFound']:return None
            raise
    def matches(obj):return obj is not None and obj.get('ContentLength')==expected_size and all(obj.get('Metadata',{}).get(k)==v for k,v in metadata.items())
    existing=head()
    if existing is not None:
        if not matches(existing):raise RemoteConflict('Existing remote object differs; no overwrite performed')
        return 'already_verified'
    try:
        client.upload_file(str(path),bucket,key,ExtraArgs={'Metadata':metadata},**({'Config':transfer_config} if transfer_config is not None else {}))
    except Exception:
        # No second PUT: if HEAD is unavailable, preserve the error and local source.
        if matches(head()):return 'verified_after_uncertain_response'
        raise
    if not matches(head()):raise RemoteVerificationFailed('Upload receipt missing or mismatched; retain local source')
    return 'uploaded_and_verified'
