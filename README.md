# API de Segurança Anti-Ataque IP para Servidores Privados de Dofus 2.0+

Esta API verifica se o IP que está acessando possui histórico de ataques. Ela realiza a verificação utilizando o seguinte URL: https://api.blocklist.de/api.php?ip=152.250.197.79. Caso algum relatório ou ataque seja encontrado, o IP é bloqueado. Em alguns casos raros, alguns IPs de determinadas pessoas podem ser denunciados mesmo sem terem feito nada de errado. Uma alternativa que encontrei é utilizar uma VPN.

## Prevenção de Perda de Jogadores

Para desativar o sistema em caso de perda de jogadores, siga estes passos:

1. Acesse o arquivo `ClientManager.cs`.
2. Na linha 385, altere `if (attacks > 0 || report > 0)` para `if (false)`.
   > Observação: Mesmo desativando isso, o sistema ainda irá detectar se um IP fez 3 tentativas de ataque em menos de 10 segundos.

## Arquivos Criados

Os seguintes arquivos são criados ou modificados:

- `ClientManager.cs` deve ser inserido no diretório do emulador: `\server\Stump.Server.BaseServer\Network\ClientManager.cs`
- `BaseClient.cs` deve ser inserido no diretório do emulador: `\server\Stump.Server.BaseServer\Network\BaseClient.cs`

O servidor pode apresentar um mínimo de lag durante um ataque, mas é improvável que ele pare de funcionar ou gere filas.
