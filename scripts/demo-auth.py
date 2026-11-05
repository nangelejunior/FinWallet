#!/usr/bin/env python3
"""Emissor RSA local para demonstração; requer Python 3 e OpenSSL. Não é serviço de identidade."""
import base64
import json
import os
from pathlib import Path
import shlex
import subprocess
import time

os.umask(0o077)
out = Path(__file__).resolve().parent.parent / 'artifacts' / 'auth'
out.mkdir(parents=True, exist_ok=True)
private = out / 'private.pem'
public = out / 'public.pem'
if not private.exists():
    subprocess.run(['openssl', 'genpkey', '-algorithm', 'RSA', '-pkeyopt', 'rsa_keygen_bits:3072', '-out', str(private)], check=True, stderr=subprocess.DEVNULL)
subprocess.run(['openssl', 'pkey', '-in', str(private), '-pubout', '-out', str(public)], check=True)
def encode(data):
    return base64.urlsafe_b64encode(data).rstrip(b'=')
def token(subject, customer, technical=False, summary_admin=False, outbox_admin=False):
    now = int(time.time())
    payload = {'iss': 'finwallet-local', 'aud': 'finwallet', 'sub': subject,
               'customer_id': customer, 'iat': now, 'nbf': now, 'exp': now + 3600}
    if technical:
        payload['permission'] = 'deposit'
    if summary_admin:
        payload['permission'] = 'summary.admin'
    if outbox_admin:
        payload['permission'] = 'outbox.admin'
    content = b'.'.join(encode(json.dumps(value, separators=(',', ':')).encode()) for value in [{'alg': 'RS256', 'typ': 'JWT'}, payload])
    signature = subprocess.run(['openssl', 'dgst', '-sha256', '-sign', str(private)], input=content, capture_output=True, check=True).stdout
    return (content + b'.' + encode(signature)).decode()
values = {
    'Authentication__Issuer': 'finwallet-local',
    'Authentication__Audience': 'finwallet',
    'Authentication__PublicKeyPem': public.read_text(),
    'OUTBOX_ADMIN_TOKEN': token('admin-outbox-demo', '55555555-5555-5555-5555-555555555555', outbox_admin=True),
    'SUMMARY_ADMIN_TOKEN': token('admin-consolidado-demo', '44444444-4444-4444-4444-444444444444', summary_admin=True),
    'TOKEN_A': token('cliente-a', '11111111-1111-1111-1111-111111111111'),
    'TOKEN_B': token('cliente-b', '22222222-2222-2222-2222-222222222222'),
    'TOKEN_TECH': token('operador-demo', '33333333-3333-3333-3333-333333333333', True),
}
(out / 'demo.env').write_text(''.join('export ' + key + '=' + shlex.quote(value) + '\n' for key, value in values.items()))
print('Credenciais com validade de 1 hora geradas. Execute: source artifacts/auth/demo.env')
