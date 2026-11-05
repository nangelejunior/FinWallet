#!/usr/bin/env python3
"""Gera identidades exclusivas para carga usando a chave RSA da demonstração local."""
import base64
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid


def encode(value):
    return base64.urlsafe_b64encode(value).rstrip(b'=')


def main():
    output = Path(sys.argv[1])
    count = int(os.environ.get('PAIRS', '8'))
    if count < 1:
        raise SystemExit('PAIRS deve ser positivo')
    key = Path(__file__).resolve().parents[2] / 'artifacts/auth/private.pem'
    if not key.is_file():
        raise SystemExit('Execute python3 scripts/demo-auth.py e carregue demo.env antes de iniciar a API.')
    now = int(time.time())

    def token():
        customer = str(uuid.uuid4())
        payload = {'iss': os.environ.get('Authentication__Issuer', 'finwallet-local'),
                   'aud': os.environ.get('Authentication__Audience', 'finwallet'),
                   'sub': f'k6-{customer}', 'customer_id': customer,
                   'iat': now, 'nbf': now, 'exp': now + 3600}
        content = b'.'.join(encode(json.dumps(v, separators=(',', ':')).encode())
                            for v in [{'alg': 'RS256', 'typ': 'JWT'}, payload])
        signature = subprocess.run(['openssl', 'dgst', '-sha256', '-sign', str(key)],
                                   input=content, capture_output=True, check=True).stdout
        return (content + b'.' + encode(signature)).decode()

    actors = [{'sourceToken': token(), 'destinationToken': token()} for _ in range(count)]
    # O arquivo contém credenciais temporárias, não faz parte dos relatórios.
    descriptor = os.open(output, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, 'w') as stream:
        json.dump(actors, stream)


if __name__ == '__main__':
    main()
