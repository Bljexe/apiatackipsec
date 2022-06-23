Essa API faz a verificação se aquele ip que está acessando tem histórico de ataque, ele verifica desse modo : https://api.blocklist.de/api.php?ip=152.250.197.79 caso apareça algum report ou atack ele barra, em alguns poucos casos alguns ips de algumas pessoas estao denunciado msm ela n ter feito nada... a alternativa que eu achei ela usar vpn.
Manter desativado em caso de perca de jogadores: 
Para desativar bastar ir no arquivo  ClientManager.cs (\server\Stump.Server.BaseServer\Network\ ClientManager.cs) linha 385 e alterar if (attacks > 0 || report > 0) para if (false)  (mesmo desativando isto o sistema de detectar que o ip fez 3 tentativa de ataque em menos de 10 segundos continua ativo)...
Arquivos criados:
ClientManager.cs (\server\Stump.Server.BaseServer\Network\ ClientManager.cs)
BaseClient.cs (emulateur 2.51\server\Stump.Server.BaseServer\Network\ BaseClient.cs)
O servidor pode ficar com um mínimo de lag quando estiver sofrendo um ataque, mas provavelmente não irar cair ou gerar filas
